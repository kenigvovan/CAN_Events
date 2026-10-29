using System.Collections.Generic;
using System.Linq;
using canevents.src.Bots;
using canevents.src.EventManagers;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace canevents.src.EventManagers.eventBehavior.Shared
{
    // Stores the health behavior with the hook: after disconnect PlayerByUid is null, so it can't be looked up to unhook.
    public sealed class DamageHookSet
    {
        private readonly Dictionary<string, (EntityBehaviorHealth health, OnDamagedDelegate hook)> hooks
            = new Dictionary<string, (EntityBehaviorHealth, OnDamagedDelegate)>();

        public void Hook(IServerPlayer player, OnDamagedDelegate hook)
            => Hook(player?.PlayerUID, player?.Entity, hook);

        // Any body, bots included (they have no IServerPlayer).
        public void Hook(string uid, Vintagestory.API.Common.Entities.Entity body, OnDamagedDelegate hook)
        {
            if (uid == null || body == null || hook == null) return;
            if (hooks.ContainsKey(uid)) return;

            var health = body.GetBehavior<EntityBehaviorHealth>();
            if (health == null) return;

            health.onDamaged += hook;
            hooks[uid] = (health, hook);
        }

        public bool IsHooked(string uid) => uid != null && hooks.ContainsKey(uid);

        // Rejoining spawns a new entity, so the old hook sits on the dead one.
        public void RehookIfStale(IServerPlayer player)
        {
            if (player == null) return;
            if (!hooks.TryGetValue(player.PlayerUID, out var entry)) return;

            var health = player.Entity?.GetBehavior<EntityBehaviorHealth>();
            if (health == null || ReferenceEquals(health, entry.health)) return;

            if (entry.health != null && entry.hook != null) entry.health.onDamaged -= entry.hook;
            health.onDamaged += entry.hook;
            hooks[player.PlayerUID] = (health, entry.hook);
        }

        // Returns the damage to apply.
        public delegate float VictimRule(string victimUid, float damage, DamageSource source);

        // Safe to call repeatedly to pick up late bodies.
        public void HookAll(RunningEvent match, IEnumerable<string> uids, VictimRule rule,
            bool includeBots = false)
        {
            if (match == null || uids == null || rule == null) return;

            foreach (var uid in uids)
            {
                if (uid == null || IsHooked(uid)) continue;
                if (!includeBots && BotIdentity.IsBot(uid)) continue;

                var body = match.BodyOf(uid);
                if (body == null) continue;

                string victimUid = uid;
                Hook(victimUid, body, (damage, source) => rule(victimUid, damage, source));
            }
        }

        public void RehookAll(RunningEvent match)
        {
            if (match == null || hooks.Count == 0) return;

            foreach (var uid in hooks.Keys.ToArray())
            {
                var health = match.BodyOf(uid)?.GetBehavior<EntityBehaviorHealth>();
                var entry = hooks[uid];
                if (health == null || ReferenceEquals(health, entry.health)) continue;

                if (entry.health != null && entry.hook != null) entry.health.onDamaged -= entry.hook;
                health.onDamaged += entry.hook;
                hooks[uid] = (health, entry.hook);
            }
        }

        // Cause entity, so the archer rather than the arrow.
        public static bool TryParticipantAttacker(RunningEvent match, DamageSource source,
            out string attackerUid, bool includeBots = false)
        {
            attackerUid = null;
            if (match == null) return false;

            var cause = source?.GetCauseEntity();
            string uid = includeBots
                ? BotMatchLink.UidOf(cause)
                : (cause as EntityPlayer)?.PlayerUID;
            if (uid == null || !match.IsParticipant(uid)) return false;

            attackerUid = uid;
            return true;
        }

        // Positive damage from another participant. False = leave the damage alone.
        public static bool TryHostileHit(RunningEvent match, string victimUid, float damage, DamageSource source,
            out string attackerUid, bool includeBots = false)
        {
            attackerUid = null;
            if (damage <= 0f) return false;
            if (!TryParticipantAttacker(match, source, out var uid, includeBots) || uid == victimUid) return false;
            attackerUid = uid;
            return true;
        }

        public void Unhook(string uid)
        {
            if (uid == null || !hooks.TryGetValue(uid, out var entry)) return;
            if (entry.health != null && entry.hook != null) entry.health.onDamaged -= entry.hook;
            hooks.Remove(uid);
        }

        public void UnhookAll()
        {
            foreach (var kvp in hooks)
            {
                if (kvp.Value.health != null && kvp.Value.hook != null)
                    kvp.Value.health.onDamaged -= kvp.Value.hook;
            }
            hooks.Clear();
        }
    }
}
