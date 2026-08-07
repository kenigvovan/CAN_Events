using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace canevents.src.EventManagers
{
    // Counting / removing / granting carried items by collectible code across a player's non-creative
    // inventories (hotbar + backpacks + character), mirroring PlayerInventoryBackup's selection.
    public static class InventoryUtil
    {
        /// <summary>Gives a stack to the player, dropping it at their feet when the inventory is full,
        /// so the item is never silently destroyed. Every grant path must go through this.</summary>
        public static void Give(ICoreServerAPI sapi, IServerPlayer player, ItemStack stack, string context = null)
        {
            if (sapi == null || player == null || stack == null) return;
            if (player.InventoryManager.TryGiveItemstack(stack)) return;

            if (player.Entity != null)
            {
                sapi.World.SpawnItemEntity(stack, player.Entity.Pos.XYZ);
                sapi.World.Logger.Warning("[canevents] {0}: dropped {1} on the ground for {2} (inventory full)",
                    context ?? "grant", stack.Collectible?.Code, player.PlayerName);
                return;
            }

            // No entity to drop at (player left mid-grant).
            sapi.World.Logger.Warning("[canevents] {0}: could not give {1} to {2} (inventory full, no entity)",
                context ?? "grant", stack.Collectible?.Code, player.PlayerName);
        }

        /// <summary>Resolves an item or block code and grants that quantity. Returns false (and logs)
        /// when the code matches neither, so callers don't announce something they didn't give.</summary>
        public static bool GiveByCode(ICoreServerAPI sapi, IServerPlayer player, string code, int quantity, string context = null)
        {
            if (sapi == null || player == null || string.IsNullOrEmpty(code) || quantity <= 0) return false;

            var item = sapi.World.GetItem(new AssetLocation(code));
            if (item != null) { Give(sapi, player, new ItemStack(item, quantity), context); return true; }

            var block = sapi.World.GetBlock(new AssetLocation(code));
            if (block != null) { Give(sapi, player, new ItemStack(block, quantity), context); return true; }

            sapi.World.Logger.Warning("[canevents] {0}: item '{1}' not found", context ?? "grant", code);
            return false;
        }

        /// <summary>Total quantity of <paramref name="code"/> the player is carrying.</summary>
        public static int Count(IServerPlayer player, string code)
        {
            if (player == null || string.IsNullOrEmpty(code)) return 0;
            int total = 0;
            foreach (var inv in player.InventoryManager.Inventories.Values)
            {
                if (inv.ClassName == "creative") continue;
                for (int i = 0; i < inv.Count; i++)
                {
                    var stack = inv[i]?.Itemstack;
                    if (stack?.Collectible?.Code != null && stack.Collectible.Code.ToString() == code)
                        total += stack.StackSize;
                }
            }
            return total;
        }

        /// <summary>True only if the player carries at least <c>qty</c> of every (code, qty) pair.</summary>
        public static bool Has(IServerPlayer player, IEnumerable<(string code, int qty)> items)
        {
            foreach (var (code, qty) in items)
                if (qty > 0 && Count(player, code) < qty) return false;
            return true;
        }

        /// <summary>Removes up to <paramref name="qty"/> of <paramref name="code"/>; returns how many
        /// were actually removed (may be less if the player no longer has enough).</summary>
        public static int Remove(IServerPlayer player, string code, int qty)
        {
            if (player == null || string.IsNullOrEmpty(code) || qty <= 0) return 0;
            int remaining = qty;
            foreach (var inv in player.InventoryManager.Inventories.Values)
            {
                if (inv.ClassName == "creative") continue;
                for (int i = 0; i < inv.Count && remaining > 0; i++)
                {
                    var slot = inv[i];
                    var stack = slot?.Itemstack;
                    if (stack?.Collectible?.Code == null || stack.Collectible.Code.ToString() != code) continue;
                    int take = Math.Min(remaining, stack.StackSize);
                    stack.StackSize -= take;
                    remaining -= take;
                    if (stack.StackSize <= 0) slot.Itemstack = null;
                    slot.MarkDirty();
                }
            }
            return qty - remaining;
        }
    }
}
