using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace canevents.src.EventManagers.eventBehavior.Shared
{
    // Puts a removed outfit piece straight back on, via the character inventory's SlotModified.
    public sealed class OutfitLock
    {
        private readonly RunningEvent runningEvent;
        private readonly IOutfitWardrobe wardrobe;

        private readonly Dictionary<string, (IInventory inv, Action<int> handler)> watchers
            = new Dictionary<string, (IInventory, Action<int>)>();

        // Guards against our own writes firing SlotModified again.
        private readonly HashSet<string> dressing = new HashSet<string>();

        public OutfitLock(RunningEvent runningEvent, IOutfitWardrobe wardrobe)
        {
            this.runningEvent = runningEvent;
            this.wardrobe = wardrobe;
        }

        private bool HasWardrobe => wardrobe?.Wardrobe != null && wardrobe.Wardrobe.Count > 0;

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

        // Reconnect gives a new inventory instance. Safe to call on every join.
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

        // Call first thing in OnEnd.
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

            // Deferred: writing a gear slot inside SlotModified re-enters (see GearSlotOrderCompat).
            runningEvent.sapi.Event.RegisterCallback(_ =>
            {
                dressing.Remove(uid);
                if (runningEvent.EventState != EventState.Running || !runningEvent.IsActive(uid)) return;

                var pl = runningEvent.sapi.World.PlayerByUid(uid) as IServerPlayer;
                if (pl?.Entity == null || !pl.Entity.Alive) return;

                EventOutfit.Dress(pl, runningEvent.sapi, wardrobe.Wardrobe,
                    wardrobe.OutfitIndexOf(uid), reclaimCarried: true);
            }, 50);
        }
    }
}
