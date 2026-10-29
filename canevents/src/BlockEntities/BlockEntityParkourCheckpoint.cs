using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace canevents.src.BlockEntities
{
    // Passive marker; ParkourEB does the detection.
    public class BlockEntityParkourCheckpoint : BlockEntity
    {
        public int Index { get; set; }
        public bool IsFinish { get; set; }

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
