using canevents.src.EventManagers;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace canevents.src.Bots
{
    // Stored in WatchedAttributes so AI tasks (created by the engine) can find the match from the entity.
    public static class BotMatchLink
    {
        public const string UidAttribute = "caneventsBotUid";

        public const string MatchIdAttribute = "caneventsBotMatch";

        // Per event, not per entity type.
        public const string DamageAttribute = "caneventsBotDamage";

        public static void Tag(Entity entity, string uid, long matchId, float damage)
        {
            entity.WatchedAttributes.SetString(UidAttribute, uid);
            entity.WatchedAttributes.SetLong(MatchIdAttribute, matchId);
            entity.WatchedAttributes.SetFloat(DamageAttribute, damage);
        }

        // Player uid or bot tag.
        public static string UidOf(Entity entity)
        {
            if (entity is EntityPlayer player) return player.PlayerUID;
            string tagged = entity?.WatchedAttributes?.GetString(UidAttribute);
            return string.IsNullOrEmpty(tagged) ? null : tagged;
        }

        public static RunningEvent MatchOf(Entity botEntity)
        {
            if (botEntity?.Api is not ICoreServerAPI sapi) return null;
            long matchId = botEntity.WatchedAttributes.GetLong(MatchIdAttribute, -1);
            if (matchId < 0) return null;

            var manager = sapi.ModLoader.GetModSystem<canevents>()?.CANEventManager;
            return manager?.FindRunningEvent(matchId);
        }

        // Vanilla targeting only checks entity code, so without this bots chase anyone nearby.
        public static bool IsOpponent(Entity self, Entity candidate)
        {
            if (Mobs.MatchMobLink.IsMatchMob(candidate)) return FightsMob(self, candidate);
            if (!TryEngage(self, candidate, out var match, out var myUid, out var theirUid)) return false;

            // Role modes decide themselves (everyone is their own team there).
            var roles = match.GetBehavior<EventManagers.eventBehavior.Shared.IBotCombatRoles>();
            if (roles != null) return roles.MayAttack(myUid, theirUid);

            // FFA gives each side its own team index.
            int myTeam = match.TeamIndexOf(myUid);
            int theirTeam = match.TeamIndexOf(theirUid);
            return myTeam < 0 || theirTeam < 0 || myTeam != theirTeam;
        }

        // Any match mob is everyone's enemy, as long as both are in the same live match.
        private static bool FightsMob(Entity self, Entity mob)
        {
            if (self == null || mob == null || !mob.Alive) return false;
            var match = MatchOf(self);
            if (match == null || match.CombatSuspended) return false;
            if (Mobs.MatchMobLink.MatchIdOf(mob) != match.Id) return false;
            string myUid = UidOf(self);
            return myUid != null && match.IsActive(myUid);
        }

        // Only role modes make bots flee.
        public static bool ShouldFlee(Entity self, Entity threat)
        {
            if (!TryEngage(self, threat, out var match, out var myUid, out var theirUid)) return false;

            var roles = match.GetBehavior<EventManagers.eventBehavior.Shared.IBotCombatRoles>();
            return roles != null && roles.ShouldFlee(myUid, theirUid);
        }

        private static bool TryEngage(Entity self, Entity other, out RunningEvent match,
            out string myUid, out string theirUid)
        {
            match = null;
            myUid = theirUid = null;
            if (self == null || other == null || other == self || !other.Alive) return false;

            match = MatchOf(self);
            if (match == null) return false;

            // Damage hooks don't cover bots, so truce/freeze works by giving them no target.
            if (match.CombatSuspended) return false;

            myUid = UidOf(self);
            theirUid = UidOf(other);
            if (myUid == null || theirUid == null || myUid == theirUid) return false;

            return match.IsActive(myUid) && match.IsActive(theirUid);
        }
    }
}
