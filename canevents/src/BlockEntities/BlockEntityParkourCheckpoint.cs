using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace canevents.src.BlockEntities
{
    public class BlockEntityParkourCheckpoint : BlockEntity
    {
        public int Index { get; set; }
        public bool IsFinish { get; set; }

        private BEBehaviorParkourCheckpoint parkourBehavior;

        public override void CreateBehaviors(Block block, IWorldAccessor worldForResolve)
        {
            base.CreateBehaviors(block, worldForResolve);
            parkourBehavior = new BEBehaviorParkourCheckpoint(this);
            Behaviors.Add(parkourBehavior);
            worldForResolve?.Logger?.Notification("[ParkourCP] CreateBehaviors at {0}", Pos);
        }

        public override void Initialize(ICoreAPI api)
        {
            base.Initialize(api);
            api?.Logger?.Notification("[ParkourCP] Initialize at {0} idx={1} finish={2} side={3}",
                Pos, Index, IsFinish, api.Side);
        }

        public void StartTicking()
        {
            Api?.Logger?.Notification("[ParkourCP] StartTicking requested at {0} (behavior={1})",
                Pos, parkourBehavior != null);
            parkourBehavior?.Start();
        }
        public void StopTicking() => parkourBehavior?.Stop();

        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            base.ToTreeAttributes(tree);
            tree.SetInt("parkourIndex", Index);
            tree.SetBool("parkourIsFinish", IsFinish);
        }

        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
        {
            base.FromTreeAttributes(tree, worldAccessForResolve);
            Index = tree.GetInt("parkourIndex", 0);
            IsFinish = tree.GetBool("parkourIsFinish", false);
        }
    }
}
