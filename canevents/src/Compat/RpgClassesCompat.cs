using System;
using System.Reflection;
using canevents.src.EventManagers;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace canevents.src.Compat
{
    // canrpgclasses decides friend or foe by canparty parties. In a match the match teams decide instead:
    // a party split over two teams must not heal or buff the enemy, and teammates outside each other's
    // party count as allies. Hooked by name through its ally resolver API, nothing breaks without the mod.
    public static class RpgClassesCompat
    {
        public const string ModId = "canrpgclasses";
        private const string ApiType = "canrpgclasses.Core.RpgCombatApi";
        // canrpgclasses AttrKeys.GuardedByPlayerUid: a pet or summon carries its owner's uid.
        private const string OwnerUidAttribute = "guardedPlayerUid";

        private static System.Func<Entity, Entity, bool?> resolver;
        private static MethodInfo resetCombat;

        public static void Apply(ICoreServerAPI sapi, CANEventManager manager)
        {
            if (!sapi.ModLoader.IsModEnabled(ModId)) return;

            var log = Log.For(sapi.World.Logger, "rpgclasses");
            resetCombat = AccessTools.Method(AccessTools.TypeByName(ApiType), "ResetCombatState");
            if (resetCombat == null)
                log.Warning("This canrpgclasses has no RpgCombatApi.ResetCombatState (API level 6); buffs and debuffs will carry into and out of matches.");

            var register = AccessTools.Method(AccessTools.TypeByName(ApiType), "RegisterAllyResolver");
            if (register == null)
            {
                log.Warning("canrpgclasses is installed but has no RpgCombatApi.RegisterAllyResolver; spells will go by parties in matches too.");
                return;
            }

            try
            {
                resolver = (a, b) => MatchAllies(manager, a, b);
                register.Invoke(null, new object[] { sapi, resolver });
                log.Notification("canrpgclasses spells take match teams as allies.");
            }
            catch (Exception e)
            {
                resolver = null;
                log.Error("Could not register the match ally rule with canrpgclasses: {0}", e);
            }
        }

        // Clean slate: every effect, control, cooldown, form, pet; called on the way into a match, at each
        // round and respawn, and on the way back out, so nothing crosses the arena wall either way.
        public static void ResetCombat(Entity entity)
        {
            if (resetCombat == null || entity == null) return;
            try { resetCombat.Invoke(null, new object[] { entity }); }
            catch (Exception e)
            {
                entity.Api?.World?.Logger?.Error("[rpgclasses] ResetCombatState failed for {0}: {1}", entity.GetName(), e);
            }
        }

        public static void Remove(ICoreServerAPI sapi)
        {
            resetCombat = null;
            if (resolver == null) return;
            AccessTools.Method(AccessTools.TypeByName(ApiType), "UnregisterAllyResolver")
                ?.Invoke(null, new object[] { sapi, resolver });
            resolver = null;
        }

        // null leaves the pair to canrpgclasses: neither side is fighting a match.
        private static bool? MatchAllies(CANEventManager manager, Entity a, Entity b)
        {
            string uidA = SideOf(a), uidB = SideOf(b);
            var matchA = Fighting(manager, uidA);
            var matchB = Fighting(manager, uidB);
            if (matchA == null && matchB == null) return null;

            // One inside, one outside (or another match): no help across the arena wall.
            if (matchA != matchB) return false;
            if (uidA == uidB) return true;

            int team = matchA.TeamIndexOf(uidA);
            return team >= 0 && team == matchA.TeamIndexOf(uidB);
        }

        // Player, bot, or the owner of a pet.
        private static string SideOf(Entity e)
        {
            string owner = e is EntityPlayer ? null : e?.WatchedAttributes?.GetString(OwnerUidAttribute);
            return string.IsNullOrEmpty(owner) ? Bots.BotMatchLink.UidOf(e) : owner;
        }

        // Before the waiting room they're still out in the world, where parties apply.
        private static RunningEvent Fighting(CANEventManager manager, string uid)
        {
            var match = manager.FindLiveEventOf(uid);
            if (match == null) return null;
            return match.EventState == EventState.Running || match.EventState == EventState.LocationFound ? match : null;
        }
    }
}
