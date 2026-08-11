using System.Reflection;
using canevents.src.EventManagers;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace canevents.src.HarmonyPatches
{
    // EB implements this to override engine respawn point while a participant is alive in the event.
    public interface IProvidesRespawnPoint
    {
        bool TryGetRespawnPoint(string playerUid, out PlayerSpawnPos pos);
    }

    public static class RespawnPatch
    {
        public static canevents Mod;

        /// <summary>One log line per session for a failing prefix; see the catch in <see cref="Prefix"/>.</summary>
        private static bool errorLogged;

        public static void Apply(Harmony harmony)
        {
            var serverPlayerType = AccessTools.TypeByName("Vintagestory.Server.ServerPlayer");
            if (serverPlayerType == null) return;
            var target = AccessTools.Method(serverPlayerType, "GetSpawnPosition", new[] { typeof(bool) });
            if (target == null) return;
            var prefix = typeof(RespawnPatch).GetMethod(nameof(Prefix), BindingFlags.Public | BindingFlags.Static);
            harmony.Patch(target, prefix: new HarmonyMethod(prefix));
        }

        public static bool Prefix(IServerPlayer __instance, ref FuzzyEntityPos __result)
        {
            try
            {
                var mgr = Mod?.CANEventManager;
                if (mgr == null) return true;
                string uid = __instance?.PlayerUID;
                if (string.IsNullOrEmpty(uid)) return true;

                // Snapshot: this runs on a respawn, which can land while a match is being started or
                // torn down; iterating the live list would throw mid-loop and drop the player back on
                // the vanilla spawn - in the middle of an arena.
                foreach (var re in mgr.RunningEventsSnapshot())
                {
                    if (re == null) continue;
                    if (re.EventState != EventState.Running) continue;
                    if (!re.IsActive(uid)) continue;
                    foreach (var eb in re.Behaviors)
                    {
                        if (eb is IProvidesRespawnPoint provider
                            && provider.TryGetRespawnPoint(uid, out var sp)
                            && sp != null && sp.y.HasValue)
                        {
                            __result = new FuzzyEntityPos(
                                sp.x + 0.5, sp.y.Value, sp.z + 0.5,
                                sp.yaw ?? 0f, sp.pitch ?? 0f, sp.roll ?? 0f)
                            {
                                Radius = 0f,
                                UsesLeft = -1
                            };
                            return false;
                        }
                    }
                }
            }
            catch (System.Exception e)
            {
                // On any error fall back to vanilla, never block respawn - but say so once. Silently
                // respawning a fighter on the world spawn used to look like a mod that "sometimes
                // forgets", with nothing in the log to work from. Once, because this runs per respawn
                // and a broken state would otherwise flood the file.
                if (!errorLogged)
                {
                    errorLogged = true;
                    Mod?.CANEventManager?.log?.Error("RespawnPatch: falling back to the vanilla spawn point: {0}", e);
                }
            }
            return true;
        }
    }
}
