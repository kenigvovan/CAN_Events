using System.Reflection;
using canevents.src.EventManagers;
using canevents.src.EventManagers.eventBehavior.Shared;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace canevents.src.HarmonyPatches
{
    // Lets a behavior override the respawn point for active participants.
    public interface IProvidesRespawnPoint
    {
        bool TryGetRespawnPoint(string playerUid, out PlayerSpawnPos pos);
    }

    public static class RespawnPatch
    {
        public static canevents Mod;

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

        // consumeSpawnUse is true only on an actual respawn; false calls just read the point.
        public static bool Prefix(IServerPlayer __instance, bool consumeSpawnUse, ref FuzzyEntityPos __result)
        {
            try
            {
                var mgr = Mod?.CANEventManager;
                if (mgr == null) return true;
                string uid = __instance?.PlayerUID;
                if (string.IsNullOrEmpty(uid)) return true;

                // Snapshot: the list can change while a match starts or ends.
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
                            // PlayerSpawnPos has no dimension, use the arena's.
                            int dim = EventSpawns.ArenaDimension(re);
                            __result = new FuzzyEntityPos(
                                sp.x + 0.5, sp.y.Value, sp.z + 0.5,
                                sp.yaw ?? 0f, sp.pitch ?? 0f, sp.roll ?? 0f)
                            {
                                Radius = 0f,
                                UsesLeft = -1,
                                Dimension = dim
                            };
                            // Engine respawn (TeleportTo) never sets Pos.Dimension, switch it ourselves.
                            var entity = __instance.Entity;
                            if (consumeSpawnUse && entity != null && entity.Pos.Dimension != dim)
                            {
                                Mod?.CANEventManager?.log?.Notification(
                                    "RespawnPatch: {0} respawns into arena dimension {1} (was {2})",
                                    __instance.PlayerName, dim, entity.Pos.Dimension);
                                entity.ChangeDimension(dim);
                            }
                            return false;
                        }
                    }
                }
            }
            catch (System.Exception e)
            {
                // Fall back to vanilla spawn, log once.
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
