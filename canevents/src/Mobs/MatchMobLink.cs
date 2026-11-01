using canevents.src.EventManagers;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace canevents.src.Mobs
{
    // A match mob is any creature spawned for a match. It is not on the roster: it has no uid,
    // earns nothing and blocks no rewards. The tag lets AI patches and cleanup find its match.
    public static class MatchMobLink
    {
        public const string MatchIdAttribute = "caneventsMobMatch";
        public const string DamageMulAttribute = "caneventsMobDamageMul";

        public static void Tag(Entity entity, long matchId, float damageMul)
        {
            entity.WatchedAttributes.SetLong(MatchIdAttribute, matchId);
            entity.WatchedAttributes.SetFloat(DamageMulAttribute, damageMul);
        }

        public static void SetDamageMul(Entity entity, float damageMul)
            => entity?.WatchedAttributes?.SetFloat(DamageMulAttribute, damageMul);

        public static bool IsMatchMob(Entity entity)
            => entity?.WatchedAttributes != null && entity.WatchedAttributes.HasAttribute(MatchIdAttribute);

        public static long MatchIdOf(Entity entity)
            => entity?.WatchedAttributes?.GetLong(MatchIdAttribute, -1) ?? -1;

        public static float DamageMulOf(Entity entity)
            => entity?.WatchedAttributes?.GetFloat(DamageMulAttribute, 1f) ?? 1f;

        public static RunningEvent MatchOf(Entity mob)
        {
            if (mob?.Api is not ICoreServerAPI sapi) return null;
            long matchId = MatchIdOf(mob);
            if (matchId < 0) return null;
            return sapi.ModLoader.GetModSystem<canevents>()?.CANEventManager?.FindRunningEvent(matchId);
        }

        // Live fighters of its own match only: spectators, the eliminated and passers-by are invisible to it.
        // An orphan (match gone, BotCleanup hasn't removed it yet) targets nobody.
        public static bool MayTarget(Entity mob, Entity target)
        {
            if (target == null || !target.Alive) return false;
            return MatchMobManager.Of(MatchIdOf(mob))?.MayTarget(mob, target) ?? false;
        }
    }
}
