using System;
using canevents.src.EventManagers.eventBehavior.Shared;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace canevents.src.EventManagers
{
    /// <summary>
    /// Applies the issuedGearDrop policy to match kit thrown on the ground.
    ///
    /// One watcher for the whole server rather than a subscription per match: an item is judged by
    /// WHO dropped it, which is a single lookup however many matches are live, and kit that leaked
    /// out of a match (a crash, a restore that failed) has no match left to hook onto at all.
    ///
    /// Two things are never touched, and both matter more than the feature itself:
    /// an unmarked item (rewards, refunds, Hunger Games loot, a player's own belongings), and an
    /// item the SERVER put on the ground - InventoryUtil.Give drops what it cannot fit, so a
    /// "remove" policy without that guard would eat the very kit it was handing out.
    /// </summary>
    public sealed class IssuedGearDropWatcher : IDisposable
    {
        private readonly ICoreServerAPI sapi;
        private readonly CANEventManager manager;
        private readonly Log log;
        private readonly EntityDelegate onEntitySpawn;

        public IssuedGearDropWatcher(ICoreServerAPI sapi, CANEventManager manager)
        {
            this.sapi = sapi;
            this.manager = manager;
            log = Log.For(sapi?.World?.Logger, "gear");

            onEntitySpawn = OnEntitySpawn;
            if (sapi?.Event != null) sapi.Event.OnEntitySpawn += onEntitySpawn;
        }

        public void Dispose()
        {
            if (sapi?.Event != null) sapi.Event.OnEntitySpawn -= onEntitySpawn;
        }

        private void OnEntitySpawn(Entity entity)
        {
            // Fires for every spawn in the world, so the cheap checks come first: a type test for
            // mobs and arrows, and one attribute read for anything that is an item at all.
            if (entity is not EntityItem item) return;
            if (!IssuedGear.IsIssued(item.Itemstack)) return;

            // Judged a tick later, because ByPlayerUid is not guaranteed to be filled in by the time
            // the spawn event fires - and that field is the whole difference between "a player threw
            // this away" and "the server could not fit it in their bag". A vanilla item cannot be
            // picked up in its first moments, so nothing can slip through the gap.
            sapi.Event.RegisterCallback(_ => log.Safely("issued gear drop", () => Judge(item)), 0);
        }

        private void Judge(EntityItem item)
        {
            if (item == null || !item.Alive || item.Itemstack == null) return;

            string byPlayerUid = item.ByPlayerUid;
            if (byPlayerUid == null) return;   // the server dropped it, not a player

            var policy = manager.PolicyOf(manager.FindLiveEventOf(byPlayerUid));
            switch (policy)
            {
                case IssuedGearDrop.Return:
                    Return(item, byPlayerUid);
                    break;
                case IssuedGearDrop.Remove:
                    Remove(item, byPlayerUid);
                    break;
                // Allow leaves it alone; Cleanup lets it lie there for the match and takes it at the
                // end (IssuedGearSweep), so dropping still works as a way to pass something along.
            }
        }

        private void Return(EntityItem item, string byPlayerUid)
        {
            var player = sapi.World.PlayerByUid(byPlayerUid) as IServerPlayer;
            if (player == null) return;   // left in that instant; the end-of-match sweep gets it

            var stack = item.Itemstack;
            // Hand it back BEFORE despawning: the other order destroys the item outright whenever
            // the give fails. Not InventoryUtil.Give either - that drops what it cannot fit, which
            // is how the item got here in the first place.
            if (!player.InventoryManager.TryGiveItemstack(stack, true))
            {
                log.Debug("Could not hand {0} back to {1} (no room); leaving it on the ground.",
                    stack.Collectible?.Code, player.PlayerName);
                return;
            }

            sapi.World.DespawnEntity(item, new EntityDespawnData { Reason = EnumDespawnReason.Removed });
        }

        private void Remove(EntityItem item, string byPlayerUid)
        {
            log.Debug("Removed issued {0} dropped by {1}.",
                item.Itemstack.Collectible?.Code, IssuedGear.IssuedTo(item.Itemstack) ?? byPlayerUid);
            sapi.World.DespawnEntity(item, new EntityDespawnData { Reason = EnumDespawnReason.Removed });
        }
    }
}
