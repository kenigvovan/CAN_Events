using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace canevents.src.Gui
{
    /// <summary>
    /// Item-code plumbing shared by the admin panel and the event browser; previously duplicated
    /// and drifted on details like block fallback and tooltips.
    /// </summary>
    public static class GuiItemUtil
    {
        /// <summary>
        /// Resolves an item code to a stack, trying items then blocks (either is valid here).
        /// Null for unknown codes, e.g. on a client missing the mod that defines them.
        /// </summary>
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

        /// <summary>
        /// The held stack as (code, quantity); false when the hand is empty. Lets the panel fill an
        /// item field by holding the thing instead of typing its code.
        /// </summary>
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

        /// <summary>
        /// Reads one of the player's own inventories as (code, quantity) pairs, merging stacks of
        /// the same item. Shared by the loadout and reward editors, which had drifting copies.
        /// </summary>
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

        /// <summary>
        /// Splits a packed "code:qty;code:qty" set into stacks, skipping unresolvable codes. The
        /// optional quantity is parsed off the LAST colon - item codes contain colons themselves.
        /// </summary>
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

        /// <summary>
        /// A packed item set as readable text ("Copper Knife x1, Charred Bread x4"), for the native
        /// GUI which has no hover tooltips. Falls back to the raw packed string when nothing resolves.
        /// </summary>
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

        /// <summary>Draws one icon, or a "?" placeholder when the code cannot be resolved.</summary>
        public static void DrawIcon(ItemIconAtlas atlas, ICoreClientAPI capi, string code,
            float size = 28f, bool tooltip = true)
        {
            if (atlas == null) { ImGui.Text("?"); return; }
            var stack = Resolve(capi, code);
            if (stack == null) { ImGui.Text("?"); return; }
            atlas.Draw(stack, new Vector2(size, size), tooltip);
        }

        /// <summary>Compact "Xh Ym" / "Ym" / "&lt;1m", for countdowns.</summary>
        public static string FormatDuration(double seconds)
        {
            if (seconds < 60) return "<1m";
            int total = (int)seconds;
            int h = total / 3600;
            int m = (total % 3600) / 60;
            return h > 0 ? string.Format("{0}h {1}m", h, m) : string.Format("{0}m", m);
        }

        /// <summary>Seconds as "Xm SSs" / "Xs" - for short, live timers.</summary>
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
