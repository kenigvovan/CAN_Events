using System;
using System.Reflection;
using canevents.src.Compat;
using HarmonyLib;
using Vintagestory.API.Common;

namespace canevents.src.HarmonyPatches
{
    // updateEyeHeight skips everything in Spectator mode, so a body shrunk by Downed stays at
    // 30% height while spectating. Patched on both sides: server owns the hitbox, client the camera.
    public static class SpectatorBodyMetricsPatch
    {
        // Singleplayer runs both sides in one process; don't patch twice.
        private static bool applied;

        private static bool errorLogged;

        public static void Apply(Harmony harmony)
        {
            if (applied || harmony == null) return;

            var target = AccessTools.Method(typeof(EntityPlayer), "updateEyeHeight");
            if (target == null) return;
            var postfix = typeof(SpectatorBodyMetricsPatch).GetMethod(nameof(Postfix),
                BindingFlags.Public | BindingFlags.Static);
            harmony.Patch(target, postfix: new HarmonyMethod(postfix));
            applied = true;
        }

        public static void Reset() => applied = false;

        public static void Postfix(EntityPlayer __instance)
        {
            try
            {
                var player = __instance?.Player;
                if (player?.WorldData == null) return;
                if (player.WorldData.CurrentGameMode != EnumGameMode.Spectator) return;

                DownedCompat.RestoreBodyMetrics(__instance);
            }
            catch (Exception e)
            {
                // Per-tick engine method: never throw, log once.
                if (!errorLogged)
                {
                    errorLogged = true;
                    __instance?.Api?.Logger?.Warning(
                        "[canevents] SpectatorBodyMetricsPatch failed, spectator body size left as-is: {0}", e);
                }
            }
        }
    }
}
