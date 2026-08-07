using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace canevents.src.BlockEntities
{
    public class BEBehaviorSpleefMelt : TickingBEBehavior
    {
        private const int TickIntervalMs = 100;

        private float meltDelay;
        private float elapsed;
        private bool _pendingResume;

        public BEBehaviorSpleefMelt(BlockEntity be) : base(be) { }

        public override void Initialize(ICoreAPI api, JsonObject properties)
        {
            base.Initialize(api, properties);
            if (_pendingResume && StartListener(TickIntervalMs)) _pendingResume = false;
        }

        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            base.ToTreeAttributes(tree);
            tree.SetFloat("meltDelay", meltDelay);
            tree.SetFloat("elapsed",   elapsed);
            tree.SetBool("melting",    Ticking);
        }

        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldForResolve)
        {
            base.FromTreeAttributes(tree, worldForResolve);
            meltDelay     = tree.GetFloat("meltDelay");
            elapsed       = tree.GetFloat("elapsed");
            _pendingResume = tree.GetBool("melting");
        }

        public void StartMelt(ICoreServerAPI sapi, float delay)
        {
            if (Ticking) return; // already melting; don't restart or stack listeners
            meltDelay = delay;
            elapsed = 0f;
            StartListener(TickIntervalMs);
            sapi.World.Logger.Notification("[SpleefMelt] StartMelt at {0}, delay={1}", Blockentity.Pos, delay);
        }

        public bool IsMelting => Ticking;

        public void ReduceDelay(float newRemainingDelay)
        {
            float remaining = meltDelay - elapsed;
            if (newRemainingDelay < remaining)
                elapsed = meltDelay - newRemainingDelay;
        }

        protected override void OnTick(float dt)
        {
            elapsed += dt;
            if (elapsed < meltDelay) return;

            var sapi = ServerApi;
            if (sapi == null)
            {
                Api?.Logger?.Warning("[SpleefMelt] OnTick fired at {0} but Api is null or not server", Blockentity.Pos);
                return;
            }

            sapi.World.Logger.Notification("[SpleefMelt] Melting block at {0}", Blockentity.Pos);
            StopListener();
            var pos = Blockentity.Pos;
            sapi.World.BlockAccessor.SetBlock(0, pos);
            sapi.World.BlockAccessor.RemoveBlockEntity(pos);
            sapi.World.BlockAccessor.TriggerNeighbourBlockUpdate(pos);
        }
    }
}
