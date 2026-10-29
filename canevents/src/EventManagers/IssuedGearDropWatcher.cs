using System;
using canevents.src.EventManagers.eventBehavior.Shared;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace canevents.src.EventManagers
{
    // issuedGearDrop policy for dropped match kit. Server-wide, judged by who dropped it.
    // Ignores unmarked items and server drops (InventoryUtil.Give overflow).
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
            if (entity is not EntityItem item) return;
            if (!IssuedGear.IsIssued(item.Itemstack)) return;

            // Next tick: ByPlayerUid may not be set yet when OnEntitySpawn fires. Fresh items can't be picked up anyway.
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
                // Cleanup is handled at match end by IssuedGearSweep.
            }
        }

        private void Return(EntityItem item, string byPlayerUid)
        {
            var player = sapi.World.PlayerByUid(byPlayerUid) as IServerPlayer;
            if (player == null) return;   // left in that instant; the end-of-match sweep gets it

            var stack = item.Itemstack;
            // Give before despawn. Not InventoryUtil.Give, it would just drop it again.
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
