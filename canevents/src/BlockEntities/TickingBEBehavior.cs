using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace canevents.src.BlockEntities
{
    /// <summary>
    /// A block entity behavior running a server-side tick listener while its block stands. Teardown
    /// must happen on both removal paths (block broken and chunk unloaded), or the tick keeps firing
    /// against an orphaned block entity - the spleef melt used to hole restored terrain that way.
    /// </summary>
    public abstract class TickingBEBehavior : BlockEntityBehavior
    {
        private long listenerId = -1;

        protected TickingBEBehavior(BlockEntity be) : base(be) { }

        /// <summary>Whether the tick is currently registered.</summary>
        protected bool Ticking => listenerId >= 0;

        /// <summary>The server API, or null on the client (these ticks are server-only).</summary>
        protected ICoreServerAPI ServerApi => Api as ICoreServerAPI;

        /// <summary>Registers the tick if not already running; false when there is nothing to register
        /// on (client side).</summary>
        protected bool StartListener(int intervalMs)
        {
            if (listenerId >= 0) return true; // never stack a second listener
            var sapi = ServerApi;
            if (sapi == null) return false;
            listenerId = sapi.Event.RegisterGameTickListener(OnTick, intervalMs);
            return true;
        }

        /// <summary>Unregisters the tick. Safe to call when it is not running.</summary>
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
