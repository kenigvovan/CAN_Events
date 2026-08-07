using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace canevents.src.BlockEntities
{
    public class BlockEntitySpleefMelt : BlockEntity
    {
        private BEBehaviorSpleefMelt meltBehavior;

        public override void CreateBehaviors(Block block, IWorldAccessor worldForResolve)
        {
            base.CreateBehaviors(block, worldForResolve);
            meltBehavior = new BEBehaviorSpleefMelt(this);
            Behaviors.Add(meltBehavior);
        }

        public void StartMelt(ICoreServerAPI sapi, float delay)
        {
            meltBehavior?.StartMelt(sapi, delay);
        }

        public void ReduceDelay(float newRemainingDelay)
        {
            meltBehavior?.ReduceDelay(newRemainingDelay);
        }

        public bool IsMelting => meltBehavior != null && meltBehavior.IsMelting;
    }
}
