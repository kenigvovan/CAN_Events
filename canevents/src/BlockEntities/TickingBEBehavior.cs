using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace canevents.src.BlockEntities
{
    // Server tick listener; stopped on both block removal and chunk unload, or it keeps firing.
    public abstract class TickingBEBehavior : BlockEntityBehavior
    {
        private long listenerId = -1;

        protected TickingBEBehavior(BlockEntity be) : base(be) { }

        protected bool Ticking => listenerId >= 0;

        protected ICoreServerAPI ServerApi => Api as ICoreServerAPI;

        // False on the client.
        protected bool StartListener(int intervalMs)
        {
            if (listenerId >= 0) return true; // never stack a second listener
            var sapi = ServerApi;
            if (sapi == null) return false;
            listenerId = sapi.Event.RegisterGameTickListener(OnTick, intervalMs);
            return true;
        }

        protected void StopListener()
        {
            if (listenerId < 0) return;
            ServerApi?.Event.UnregisterGameTickListener(listenerId);
            listenerId = -1;
        }

        protected abstract void OnTick(float dt);

        public override void OnBlockRemoved()
        {
            StopListener();
            base.OnBlockRemoved();
        }

        public override void OnBlockUnloaded()
        {
            StopListener();
            base.OnBlockUnloaded();
        }
    }
}
