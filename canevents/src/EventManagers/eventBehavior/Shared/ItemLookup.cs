using Vintagestory.API.Common;

namespace canevents.src.EventManagers.eventBehavior.Shared
{
    // Config codes can be items or blocks; tries items first.
    public static class ItemLookup
    {
        public static ItemStack StackOf(IWorldAccessor world, string code, int quantity = 1)
        {
            if (world == null || string.IsNullOrEmpty(code)) return null;
            var loc = new AssetLocation(code);
            var item = world.GetItem(loc);
            if (item != null) return new ItemStack(item, quantity);
            var block = world.GetBlock(loc);
            return block != null ? new ItemStack(block, quantity) : null;
        }
    }
}
