using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace canevents.src.EventManagers.eventBehavior.Shared
{
    // When a player was last hurt by a creature or another player. Walking into or out of an arena
    // is refused for a while after that, so the arena can't be used to escape a fight with one's gear.
    public static class CombatTag
    {
        private static readonly object sync = new object();
        private static readonly Dictionary<string, long> lastHitMs = new Dictionary<string, long>();

        // Once per player entity (login spawns a fresh one); falls, hunger and the like don't count.
        public static void Track(ICoreServerAPI sapi, IServerPlayer player)
        {
            var health = player?.Entity?.GetBehavior<EntityBehaviorHealth>();
            if (health == null) return;
            string uid = player.PlayerUID;
            health.onDamaged += (damage, source) =>
            {
                if (damage > 0f && source?.GetCauseEntity() != null)
                    lock (sync) lastHitMs[uid] = sapi.World.ElapsedMilliseconds;
                return damage;
            };
        }

        // 0 = free to go.
        public static int SecondsLeft(ICoreServerAPI sapi, string uid, int tagSeconds)
        {
            if (uid == null || tagSeconds <= 0) return 0;
            long hit;
            lock (sync)
                if (!lastHitMs.TryGetValue(uid, out hit)) return 0;
            long left = tagSeconds * 1000L - (sapi.World.ElapsedMilliseconds - hit);
            return left > 0 ? (int)Math.Ceiling(left / 1000.0) : 0;
        }

        // Static state would survive a world reload in the same process.
        public static void Reset()
        {
            lock (sync) lastHitMs.Clear();
        }
    }
}
