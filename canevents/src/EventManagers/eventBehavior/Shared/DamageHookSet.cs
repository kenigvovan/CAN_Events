using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace canevents.src.EventManagers.eventBehavior.Shared
{
    /// <summary>
    /// Tracked set of <c>onDamaged</c> hooks, with unhooking that survives a disconnect.
    /// The (health, hook) pair is stored rather than re-derived: after a player leaves
    /// <c>PlayerByUid</c> returns null while the <see cref="EntityBehaviorHealth"/> instance
    /// lives on, so a lookup at teardown would silently leak the delegate.
    /// </summary>
    public sealed class DamageHookSet
    {
        private readonly Dictionary<string, (EntityBehaviorHealth health, OnDamagedDelegate hook)> hooks
            = new Dictionary<string, (EntityBehaviorHealth, OnDamagedDelegate)>();

        /// <summary>
        /// Attaches the hook to the player's health behavior. No-op when already hooked or
        /// there is no health behavior.
        /// </summary>
        public void Hook(IServerPlayer player, OnDamagedDelegate hook)
            => Hook(player?.PlayerUID, player?.Entity, hook);

        /// <summary>
        /// Hooks any body, bots included. A bot has no player object, so a rule enforced only on
        /// IServerPlayer victims (no friendly fire, no fighting) simply does not apply to hitting
        /// a bot - and a lobby topped up with bots is the normal case, not an edge one.
        /// </summary>
        public void Hook(string uid, Vintagestory.API.Common.Entities.Entity body, OnDamagedDelegate hook)
        {
            if (uid == null || body == null || hook == null) return;
            if (hooks.ContainsKey(uid)) return;

            var health = body.GetBehavior<EntityBehaviorHealth>();
            if (health == null) return;

            health.onDamaged += hook;
            hooks[uid] = (health, hook);
        }

        /// <summary>Is this uid already hooked? For a late body (a bot whose chunk had not arrived
        /// at match start) that has to be picked up on a later tick.</summary>
        public bool IsHooked(string uid) => uid != null && hooks.ContainsKey(uid);

        /// <summary>
        /// Re-attaches the hook after a reconnect: rejoining spawns a FRESH entity, so the stored
        /// delegate hangs on the dead entity while the uid still looks hooked, leaving the new body
        /// unprotected. No-op when the stored health is the live one or the player was never hooked.
        /// </summary>
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

        /// <summary>Detaches every hook. Safe to call more than once.</summary>
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
