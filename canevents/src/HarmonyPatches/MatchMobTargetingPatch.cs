using System;
using canevents.src.Mobs;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace canevents.src.HarmonyPatches
{
    // Every vanilla targeting task (seek, melee, shoot, both the old and the "R" task families) asks
    // CanSense before picking a target. For a match mob the answer is narrowed to what its match's
    // director allows, so any creature can be used as a match mob without editing its JSON.
    // Overrides that skip base.CanSense are not covered.
    public static class MatchMobTargetingPatch
    {
        public static void Apply(Harmony harmony, ICoreServerAPI sapi)
        {
            var log = Log.For(sapi?.World?.Logger, "mobs");
            try
            {
                harmony.Patch(AccessTools.Method(typeof(AiTaskBaseTargetable), "CanSense"),
                    postfix: new HarmonyMethod(typeof(MatchMobTargetingPatch), nameof(OldFamily)));
                harmony.Patch(AccessTools.Method(typeof(AiTaskBaseTargetableR), "CanSense"),
                    postfix: new HarmonyMethod(typeof(MatchMobTargetingPatch), nameof(RFamily)));
            }
            catch (Exception e)
            {
                log.Error("Could not install the match mob targeting filter; match mobs will attack anyone nearby: {0}", e);
            }
        }

        public static void OldFamily(EntityAgent ___entity, Entity e, ref bool __result)
        {
            if (__result && MatchMobLink.IsMatchMob(___entity)) __result = MatchMobLink.MayTarget(___entity, e);
        }

        public static void RFamily(EntityAgent ___entity, Entity target, ref bool __result)
        {
            if (__result && MatchMobLink.IsMatchMob(___entity)) __result = MatchMobLink.MayTarget(___entity, target);
        }
    }
}
