using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace canevents.src.Gui
{
    public static class GuiItemUtil
    {
        // Items first, then blocks. Null if the client lacks the mod defining the code.
        public static ItemStack Resolve(ICoreClientAPI capi, string code, int quantity = 1)
        {
            if (capi == null || string.IsNullOrEmpty(code)) return null;

            var loc = new AssetLocation(code);
            var item = capi.World.GetItem(loc);
            if (item != null) return new ItemStack(item, quantity);

            var block = capi.World.GetBlock(loc);
            if (block != null) return new ItemStack(block, quantity);

            return null;
        }

        public static bool TryReadHeldItem(ICoreClientAPI capi, out string code, out int quantity)
        {
            code = "";
            quantity = 0;

            var stack = capi?.World.Player?.InventoryManager?.ActiveHotbarSlot?.Itemstack;
            if (stack?.Collectible?.Code == null) return false;

            code = stack.Collectible.Code.ToString();
            quantity = stack.StackSize;
            return true;
        }

        // Stacks of the same code are merged.
        public static List<(string code, int qty)> ReadInventory(ICoreClientAPI capi, string inventoryClassName)
        {
            var result = new List<(string code, int qty)>();
            var inv = capi?.World.Player?.InventoryManager.GetOwnInventory(inventoryClassName);
            if (inv == null) return result;

            for (int i = 0; i < inv.Count; i++)
            {
                var stack = inv[i]?.Itemstack;
                if (stack?.Collectible?.Code == null) continue;

                string code = stack.Collectible.Code.ToString();
                int idx = result.FindIndex(x => x.code == code);
                if (idx >= 0) result[idx] = (code, result[idx].qty + stack.StackSize);
                else result.Add((code, stack.StackSize));
            }
            return result;
        }

        // "code:qty;code:qty". Qty is after the last colon, codes have colons too.
        public static List<ItemStack> ResolvePacked(ICoreClientAPI capi, string packed)
        {
            var result = new List<ItemStack>();
            if (string.IsNullOrEmpty(packed)) return result;

            foreach (var entry in packed.Split(';'))
            {
                if (string.IsNullOrEmpty(entry)) continue;

                string code = entry;
                int quantity = 1;
                int lastColon = entry.LastIndexOf(':');
                if (lastColon > 0 && int.TryParse(entry.Substring(lastColon + 1), out int qty))
                {
                    code = entry.Substring(0, lastColon);
                    quantity = qty;
                }

                var stack = Resolve(capi, code, quantity);
                if (stack != null) result.Add(stack);
            }
            return result;
        }

        // For the native GUI, which has no item tooltips.
        public static string DescribePacked(ICoreClientAPI capi, string packed)
        {
            var stacks = ResolvePacked(capi, packed);
            if (stacks.Count == 0) return packed ?? "";

            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < stacks.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(stacks[i].GetName());
                if (stacks[i].StackSize > 1) sb.Append(" x").Append(stacks[i].StackSize);
            }
            return sb.ToString();
        }

        public static string FormatDuration(double seconds)
        {
            if (seconds < 60) return "<1m";
            int total = (int)seconds;
            int h = total / 3600;
            int m = (total % 3600) / 60;
            return h > 0 ? string.Format("{0}h {1}m", h, m) : string.Format("{0}m", m);
        }

        public static string FormatShortDuration(float seconds)
        {
            if (seconds < 0) return "-";
            int total = (int)seconds;
            return total >= 60
                ? string.Format("{0}m {1:00}s", total / 60, total % 60)
                : total + "s";
        }

        public static string Ordinal(int n)
        {
            int mod100 = n % 100;
            if (mod100 >= 11 && mod100 <= 13) return n + "th";
            switch (n % 10)
            {
                case 1: return n + "st";
                case 2: return n + "nd";
                case 3: return n + "rd";
                default: return n + "th";
            }
        }
    }
}
