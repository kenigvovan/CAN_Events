using System;
using System.Reflection;
using canevents.src.EventManagers;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace canevents.src.HarmonyPatches
{
    // Skips death side effects of other mods (XSkills xp loss, PlayerCorpse, Gravestones) and the
    // vanilla drop for match participants. Types are looked up by name, so missing mods are fine.
    // Gravestones/vanilla queue their work to the main thread and would grab the restored inventory.
    public static class MatchDeathExternalsPatch
    {
        public static canevents Mod;

        private static bool errorLogged;

        // SkipVanillaDrop has no api argument to log through.
        private static ICoreServerAPI api;

        public static void Apply(Harmony harmony, ICoreServerAPI sapi)
        {
            api = sapi;
            var log = Log.For(sapi?.World?.Logger, "match-death");

            // OnEntityDeath only enqueues "dropinventoryondeath"; main-thread tasks run after tick
            // listeners (ServerMain.Process), so a match-ending death can drop the restored inventory.
            // keepContents can't be used, it's shared by the whole player entity type.
            Patch(harmony, log, "Vintagestory.GameContent.EntityBehaviorPlayerInventory", "OnEntityDeath",
                nameof(SkipVanillaDrop),
                "Vanilla: a death in a match no longer queues the engine's own inventory drop.");

            // Private; matched by name, it's the only OnPlayerDeath there.
            Patch(harmony, log, "XLib.XLeveling.XLevelingServer", "OnPlayerDeath",
                nameof(SkipSkillLoss),
                "XSkills: losing a match no longer costs skill experience.");

            // Private one-parameter overload, not the OnEntityDeath handler.
            Patch(harmony, log, "PlayerCorpse.Systems.DeathContentManager", "OnPlayerDeath",
                nameof(SkipCorpse),
                "PlayerCorpse: no corpse is left in an arena for a death in a match.");

            // Patch the queue call, not the queued work: by then the match may already be over.
            Patch(harmony, log, "Gravestones.GravestonesModSystem", "QueueGravestoneForDeath",
                nameof(SkipGravestone),
                "Gravestones: no gravestone is left in an arena for a death in a match.");
        }

        private static void Patch(Harmony harmony, Log log, string typeName, string methodName,
            string prefixName, string what)
        {
            try
            {
                var type = AccessTools.TypeByName(typeName);
                if (type == null) return;   // mod not installed

                var target = AccessTools.Method(type, methodName);
                if (target == null)
                {
                    log.Warning("{0} is installed but has no {1}.{2} to patch; its death handling is "
                        + "left alone and a match death will be treated as an ordinary one.",
                        typeName, typeName, methodName);
                    return;
                }

                var prefix = typeof(MatchDeathExternalsPatch)
                    .GetMethod(prefixName, BindingFlags.Public | BindingFlags.Static);
                harmony.Patch(target, prefix: new HarmonyMethod(prefix));
                log.Notification(what);
            }
            catch (Exception e)
            {
                log.Warning("Could not patch {0}.{1}: {2}", typeName, methodName, e.Message);
            }
        }

        // Own-gear matches keep the drop. __instance is the base type so we don't depend on the concrete one.
        public static bool SkipVanillaDrop(EntityBehavior __instance)
        {
            var player = (__instance?.entity as EntityPlayer)?.Player as IServerPlayer;
            if (player == null) return true;

            var re = MatchOf(player);
            if (re == null) return true;

            var save = re.GetBehavior<EventManagers.eventBehavior.SavePlayerAttributes>();
            if (save == null || save.KeepOwnGear) return true;

            ReportLeftovers(player, re);
            return false;
        }

        // Runs inside Die() after SavePlayerAttributes swept; anything left here means the sweep missed.
        private static void ReportLeftovers(IServerPlayer player, RunningEvent re)
        {
            try
            {
                int carried = EventManagers.PlayerInventoryBackup.CountCarried(player, out string what);
                if (carried == 0) return;

                Log.For(api?.World?.Logger, "inventory").Warning(
                    "{0} died in '{1}' still carrying {2} stack(s) - the match's death sweep missed them. "
                    + "The engine's drop is suppressed, so they stay on the player: {3}",
                    player.PlayerName, re.Event?.Code ?? "?", carried, what);
            }
            catch { /* never throw inside a death */ }
        }

        // Harmony binds by parameter name, keep "byPlayer". Applies to own-gear matches too.
        public static bool SkipSkillLoss(IServerPlayer byPlayer) => MatchOf(byPlayer) == null;

        // Only for issued-kit matches; own-gear matches still get a corpse.
        public static bool SkipCorpse(IServerPlayer byPlayer)
        {
            var re = MatchOf(byPlayer);
            if (re == null) return true;

            bool ownGear = re.GetBehavior<EventManagers.eventBehavior.SavePlayerAttributes>()?.KeepOwnGear ?? false;
            return ownGear;
        }

        // Same rule as SkipCorpse. Parameter name must stay "player" (Harmony binds by name).
        public static bool SkipGravestone(IServerPlayer player)
        {
            var re = MatchOf(player);
            if (re == null) return true;

            bool ownGear = re.GetBehavior<EventManagers.eventBehavior.SavePlayerAttributes>()?.KeepOwnGear ?? false;
            return ownGear;
        }

        // Checks participation, not "still alive": our own death handler may have eliminated them first.
        public static RunningEvent MatchOf(IServerPlayer player)
        {
            try
            {
                var mgr = Mod?.CANEventManager;
                string uid = player?.PlayerUID;
                if (mgr == null || string.IsNullOrEmpty(uid)) return null;

                // Snapshot, the list can change mid-death.
                foreach (var re in mgr.RunningEventsSnapshot())
                {
                    if (re == null) continue;
                    if (re.EventState != EventState.Running) continue;
                    if (re.HoldsPlayer(uid)) return re;
                }
            }
            catch (Exception e)
            {
                // On error, treat it as a normal death.
                if (!errorLogged)
                {
                    errorLogged = true;
                    Mod?.CANEventManager?.log?.Error(
                        "MatchDeathExternalsPatch: could not tell whether {0} died in a match, so the "
                        + "death is being treated as an ordinary one: {1}",
                        player?.PlayerName ?? "<unknown>", e);
                }
            }
            return null;
        }
    }
}
