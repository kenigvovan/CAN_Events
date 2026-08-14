using canevents.src.EventManagers;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace canevents.src.Bots
{
    /// <summary>
    /// Ties a spawned bot body back to its match and answers "is that entity a legal target?".
    /// The link lives on WatchedAttributes rather than in a dictionary, so engine-constructed AI
    /// tasks can resolve it from the entity alone.
    /// </summary>
    public static class BotMatchLink
    {
        /// <summary>Bot's match uid, the one the roster knows it by.</summary>
        public const string UidAttribute = "caneventsBotUid";

        /// <summary>Id of the RunningEvent the bot was spawned for.</summary>
        public const string MatchIdAttribute = "caneventsBotMatch";

        /// <summary>Melee damage for this bot, set per event rather than per entity type.</summary>
        public const string DamageAttribute = "caneventsBotDamage";

        public static void Tag(Entity entity, string uid, long matchId, float damage)
        {
            entity.WatchedAttributes.SetString(UidAttribute, uid);
            entity.WatchedAttributes.SetLong(MatchIdAttribute, matchId);
            entity.WatchedAttributes.SetFloat(DamageAttribute, damage);
        }

        /// <summary>The match uid of any participant body: a player's account uid, or a bot's tag.</summary>
        public static string UidOf(Entity entity)
        {
            if (entity is EntityPlayer player) return player.PlayerUID;
            string tagged = entity?.WatchedAttributes?.GetString(UidAttribute);
            return string.IsNullOrEmpty(tagged) ? null : tagged;
        }

        /// <summary>The live match a bot body belongs to, or null once it is over.</summary>
        public static RunningEvent MatchOf(Entity botEntity)
        {
            if (botEntity?.Api is not ICoreServerAPI sapi) return null;
            long matchId = botEntity.WatchedAttributes.GetLong(MatchIdAttribute, -1);
            if (matchId < 0) return null;

            var manager = sapi.ModLoader.GetModSystem<canevents>()?.CANEventManager;
            return manager?.FindRunningEvent(matchId);
        }

        /// <summary>
        /// Legal target = both still in play in the same match, on different teams. Without it a bot
        /// would chase any passer-by: vanilla targeting only matches on entity code.
        /// </summary>
        public static bool IsOpponent(Entity self, Entity candidate)
        {
            if (self == null || candidate == null || candidate == self) return false;
            if (!candidate.Alive) return false;

            var match = MatchOf(self);
            if (match == null) return false;

            // Truce/freeze apply to bots too. Players are held by damage hooks, which never fire for
            // a bot, so a bot is held by having no legal target at all.
            if (match.CombatSuspended) return false;

            string myUid = UidOf(self);
            string theirUid = UidOf(candidate);
            if (myUid == null || theirUid == null || myUid == theirUid) return false;

            // An eliminated player still standing in the arena is no longer a target.
            if (!match.IsActive(myUid) || !match.IsActive(theirUid)) return false;

            // A mode with hidden roles decides for itself: in a free-for-all everyone is their own
            // team, so the team rule alone would send the townsfolk charging at each other.
            var roles = match.GetBehavior<EventManagers.eventBehavior.Shared.IBotCombatRoles>();
            if (roles != null) return roles.MayAttack(myUid, theirUid);

            // Free-for-all gives every side its own team index, so this also covers solo modes.
            int myTeam = match.TeamIndexOf(myUid);
            int theirTeam = match.TeamIndexOf(theirUid);
            return myTeam < 0 || theirTeam < 0 || myTeam != theirTeam;
        }

        /// <summary>Does this bot have reason to run from that entity? Only a mode with roles has an
        /// opinion; everywhere else bots stand their ground, as they always did.</summary>
        public static bool ShouldFlee(Entity self, Entity threat)
        {
            if (self == null || threat == null || threat == self) return false;
            if (!threat.Alive) return false;

            var match = MatchOf(self);
            if (match == null || match.CombatSuspended) return false;

            var roles = match.GetBehavior<EventManagers.eventBehavior.Shared.IBotCombatRoles>();
            if (roles == null) return false;

            string myUid = UidOf(self);
            string theirUid = UidOf(threat);
            if (myUid == null || theirUid == null || myUid == theirUid) return false;
            if (!match.IsActive(myUid) || !match.IsActive(theirUid)) return false;

            return roles.ShouldFlee(myUid, theirUid);
        }
    }
}
