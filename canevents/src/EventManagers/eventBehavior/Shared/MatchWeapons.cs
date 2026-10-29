using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace canevents.src.EventManagers.eventBehavior.Shared
{
    // Hide, confiscate or mark-lootable the weapons a player carries.
    public static class MatchWeapons
    {
        // Tool covers knife and bow; AttackPower > 1 catches clubs and rocks.
        public static bool IsWeapon(ItemStack stack) => IsWeapon(stack?.Collectible);

        public static bool IsWeapon(CollectibleObject collectible)
            => collectible != null && (collectible.Tool != null || collectible.AttackPower > 1f);

        public static bool IsAmmo(ItemStack stack)
        {
            var collectible = stack?.Collectible;
            if (collectible == null) return false;
            if (collectible is ItemArrow) return true;

            // Modded ammo isn't always ItemArrow; go by code path.
            var path = collectible.Code?.Path;
            return path != null && (path.Contains("arrow") || path.Contains("bolt"));
        }

        public static bool IsTelltale(ItemStack stack) => IsWeapon(stack) || IsAmmo(stack);

        // Held items are visible to others. Moves the weapon away and fills the hand with something harmless.
        public static void MoveOutOfHand(IServerPlayer player)
        {
            var hotbar = player?.InventoryManager?.GetOwnInventory(GlobalConstants.hotBarInvClassName);
            if (hotbar == null) return;

            // Slot 10+ is off-hand etc, not the hotbar row (see LoadoutService).
            int lastSlot = Math.Min(9, hotbar.Count - 1);
            if (lastSlot < 1) return;

            int active = player.InventoryManager.ActiveHotbarSlotNumber;
            if (active < 0 || active > lastSlot) active = 0;

            var handSlot = hotbar[active];
            if (IsTelltale(handSlot?.Itemstack))
            {
                for (int i = lastSlot; i >= 0; i--)
                {
                    if (i == active || hotbar[i].Itemstack != null) continue;
                    hotbar[i].Itemstack = handSlot.Itemstack;
                    handSlot.Itemstack = null;
                    hotbar[i].MarkDirty();
                    handSlot.MarkDirty();
                    break;
                }
            }

            if (handSlot?.Itemstack != null) return;
            for (int i = 0; i <= lastSlot; i++)
            {
                if (i == active) continue;
                var stack = hotbar[i].Itemstack;
                if (stack == null || IsTelltale(stack)) continue;
                handSlot.Itemstack = stack;
                hotbar[i].Itemstack = null;
                handSlot.MarkDirty();
                hotbar[i].MarkDirty();
                break;
            }
        }

        // Returns the taken stacks so the same items can be given back later.
        public static List<ItemStack> TakeAll(IServerPlayer player)
        {
            var taken = new List<ItemStack>();
            if (player?.InventoryManager == null) return taken;

            foreach (var inv in player.InventoryManager.Inventories.Values)
            {
                if (inv.ClassName == "creative") continue;
                if (inv.ClassName == GlobalConstants.characterInvClassName) continue;   // worn clothes

                for (int i = 0; i < inv.Count; i++)
                {
                    var slot = inv[i];
                    if (!IsTelltale(slot?.Itemstack)) continue;
                    taken.Add(slot.Itemstack);
                    slot.Itemstack = null;
                    slot.MarkDirty();
                }
            }
            return taken;
        }

        // Issued gear is normally swept before the body drops; this makes weapons drop instead.
        public static void MarkLootable(IServerPlayer player)
        {
            if (player?.InventoryManager == null) return;

            foreach (var inv in player.InventoryManager.Inventories.Values)
            {
                if (inv.ClassName == "creative") continue;
                if (inv.ClassName == GlobalConstants.characterInvClassName) continue;

                for (int i = 0; i < inv.Count; i++)
                {
                    var stack = inv[i]?.Itemstack;
                    if (IsTelltale(stack)) IssuedGear.MarkDropOnDeath(stack);
                }
            }
        }
    }
}
