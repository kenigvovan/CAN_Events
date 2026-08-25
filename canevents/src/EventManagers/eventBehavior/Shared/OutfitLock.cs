using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace canevents.src.EventManagers.eventBehavior.Shared
{
    /// <summary>
    /// Keeps the match outfit on the people wearing it: take a piece off and it goes straight back.
    ///
    /// For a mode where the clothes carry information - in Murder Mystery the colour IS the
    /// description everyone testifies by ("the murderer wore red") - undressing is not a wardrobe
    /// choice, it erases the only evidence there is. Nothing in the game stops a player from opening
    /// their character inventory and dropping the coat, so this listens for that and puts it back.
    ///
    /// Event-driven: the inventory says when it changed, so nothing is polled. Knows nothing about
    /// roles or modes - the wardrobe and who wears which outfit come from <see cref="IOutfitWardrobe"/>.
    /// </summary>
    public sealed class OutfitLock
    {
        private readonly RunningEvent runningEvent;
        private readonly IOutfitWardrobe wardrobe;

        /// <summary>Live subscriptions per player, kept so they can be taken off again: a listener
        /// left behind would go on re-dressing people after the match.</summary>
        private readonly Dictionary<string, (IInventory inv, Action<int> handler)> watchers
            = new Dictionary<string, (IInventory, Action<int>)>();

        /// <summary>Who we are dressing right now, so our own writes do not come back through
        /// SlotModified as another reason to dress.</summary>
        private readonly HashSet<string> dressing = new HashSet<string>();

        public OutfitLock(RunningEvent runningEvent, IOutfitWardrobe wardrobe)
        {
            this.runningEvent = runningEvent;
            this.wardrobe = wardrobe;
        }

        private bool HasWardrobe => wardrobe?.Wardrobe != null && wardrobe.Wardrobe.Count > 0;

        /// <summary>Starts watching this player. No-op when already watched or there is nothing to
        /// dress them in.</summary>
        public void Watch(IServerPlayer player)
        {
            if (player == null || !HasWardrobe) return;

            string uid = player.PlayerUID;
            if (watchers.ContainsKey(uid)) return;

            var inv = player.InventoryManager?.GetOwnInventory(GlobalConstants.characterInvClassName);
            if (inv == null) return;

            Action<int> handler = _ => Redress(uid);
            inv.SlotModified += handler;
            watchers[uid] = (inv, handler);
        }

        /// <summary>
        /// Moves the listener onto the inventory the player actually has now. A reconnect brings a
        /// fresh one, and the old subscription would sit on an inventory nobody uses. No-op while it
        /// is the same one, so this is safe to call on every join.
        /// </summary>
        public void Rewatch(IServerPlayer player)
        {
            if (player == null) return;
            string uid = player.PlayerUID;
            var inv = player.InventoryManager?.GetOwnInventory(GlobalConstants.characterInvClassName);

            if (watchers.TryGetValue(uid, out var existing))
            {
                if (ReferenceEquals(existing.inv, inv)) return;
                if (existing.inv != null && existing.handler != null)
                    existing.inv.SlotModified -= existing.handler;
                watchers.Remove(uid);
            }
            Watch(player);
        }

        /// <summary>Stops watching everyone. Belongs in OnEnd, first thing.</summary>
        public void UnwatchAll()
        {
            foreach (var kvp in watchers)
            {
                if (kvp.Value.inv != null && kvp.Value.handler != null)
                    kvp.Value.inv.SlotModified -= kvp.Value.handler;
            }
            watchers.Clear();
            dressing.Clear();
        }

        private void Redress(string uid)
        {
            if (!dressing.Add(uid)) return;

            // Deferred by a tick rather than done here: the inventory is mid-change inside its own
            // SlotModified, and writing a clothing slot from that notification is the re-entry
            // GearSlotOrderCompat exists to prevent.
            runningEvent.sapi.Event.RegisterCallback(_ =>
            {
                dressing.Remove(uid);
                if (runningEvent.EventState != EventState.Running || !runningEvent.IsActive(uid)) return;

                var pl = runningEvent.sapi.World.PlayerByUid(uid) as IServerPlayer;
                if (pl?.Entity == null || !pl.Entity.Alive) return;

                // reclaimCarried: the very piece they took off goes back on rather than a copy -
                // otherwise stashing the coat in the backpack leaves them wearing a second one.
                // Dress only fills empty clothing slots, so nothing else is disturbed.
                EventOutfit.Dress(pl, runningEvent.sapi, wardrobe.Wardrobe,
                    wardrobe.OutfitIndexOf(uid), reclaimCarried: true);
            }, 50);
        }
    }
}
