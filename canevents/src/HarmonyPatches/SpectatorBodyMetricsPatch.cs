using System;
using System.Reflection;
using canevents.src.Compat;
using HarmonyLib;
using Vintagestory.API.Common;

namespace canevents.src.HarmonyPatches
{
    /// <summary>
    /// Keeps a spectator's body at its normal size.
    ///
    /// <c>EntityPlayer.updateEyeHeight</c> is what grows eye height and the collision/selection boxes
    /// back after anything squeezed them (crouching, dying, or another mod's crawl state). Its whole
    /// body is skipped in Spectator mode - the engine has no use for a spectator's hitbox - so any
    /// value that was clamped at the moment we seat someone as a spectator stays clamped for as long
    /// as they spectate.
    ///
    /// That only shows up next to a mod that clamps those values, and one such mod is Downed: a player
    /// executed while knocked down goes into our spectator seat at 30% of their height, with the
    /// camera down at their knees. Restoring here rather than once on entry covers the ordering both
    /// ways, and costs a handful of float comparisons per tick otherwise.
    ///
    /// Applied on both sides on purpose: the server owns the collision box, the client owns the
    /// camera, and each runs its own copy of this method.
    /// </summary>
    public static class SpectatorBodyMetricsPatch
    {
        // Single-player runs both sides in one process against the same method table; without this the
        // postfix would be installed twice and run twice per tick.
        private static bool applied;

        /// <summary>One log line per session for a failing postfix; see the catch in <see cref="Postfix"/>.</summary>
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

        /// <summary>Call from Dispose so a hot-reload re-applies the patch instead of skipping it.</summary>
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
                // A spectator with a wrong-sized hitbox is a cosmetic problem; throwing out of a
                // per-tick engine method is not. Log once - this runs every tick and would flood.
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
