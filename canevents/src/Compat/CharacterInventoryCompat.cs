using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.Common;
using Vintagestory.GameContent;

namespace canevents.src.Compat
{
    // Combat Overhaul (and forks, modid varies) disables vanilla armour slots 12-14 (DrawUnavailable).
    // Writing there by index crashes ModSystemWearableStats.handleDamaged on every hit, so never seat by index.
    public static class CharacterInventoryCompat
    {
        // DrawUnavailable = switched off by the inventory, regardless of CanHold.
        public static bool IsUsable(ItemSlot slot) => slot != null && !slot.DrawUnavailable;

        // dressType: vanilla-only fallback, CanHold ignores clothesCategoryByType.
        // emptyOnly: don't displace worn pieces (outfits on top of loadout armour).
        public static ItemSlot FindWearSlot(IInventory inv, ItemStack stack,
            EnumCharacterDressType? dressType = null, bool emptyOnly = false)
        {
            if (inv == null || stack == null) return null;

            var dummy = new DummySlot(stack);

            for (int i = 0; i < inv.Count; i++)
            {
                var slot = inv[i];
                if (!IsUsable(slot) || slot.Itemstack != null) continue;
                if (slot.CanHold(dummy)) return slot;
            }

            // Last matching slot, occupied or not.
            if (!emptyOnly)
            {
                for (int i = inv.Count - 1; i >= 0; i--)
                {
                    var slot = inv[i];
                    if (IsUsable(slot) && slot.CanHold(dummy)) return slot;
                }
            }

            // Vanilla-only: route by dress type index.
            if (dressType != null && IsVanilla(inv))
            {
                int index = (int)dressType.Value;
                if (index >= 0 && index < inv.Count)
                {
                    var slot = inv[index];
                    if (IsUsable(slot) && (!emptyOnly || slot.Itemstack == null)) return slot;
                }
            }

            return null;
        }

        // No MarkDirty here; callers batch through GearSlotOrderCompat.MarkDirtyAfterFill (CO cares about order).
        public static ItemSlot Wear(IInventory inv, ItemStack stack,
            EnumCharacterDressType? dressType = null, bool emptyOnly = false)
        {
            var slot = FindWearSlot(inv, stack, dressType, emptyOnly);
            if (slot != null) slot.Itemstack = stack;
            return slot;
        }

        // Exact type: a subclass is some mod's replacement.
        public static bool IsVanilla(IInventory inv)
            => inv != null && inv.GetType() == typeof(InventoryCharacter);

        // On join: cleans up pieces older versions put into disabled slots (crash on every hit).
        public static int ClearUnusableSlots(IServerPlayer player, ILogger log)
        {
            var inv = player?.InventoryManager?.GetOwnInventory(GlobalConstants.characterInvClassName);
            if (inv == null) return 0;

            var emptied = new List<(ItemSlot slot, ItemStack removed)>();
            for (int i = 0; i < inv.Count; i++)
            {
                var slot = inv[i];
                if (slot == null || slot.Itemstack == null || IsUsable(slot)) continue;

                var stack = slot.Itemstack;
                slot.Itemstack = null;
                emptied.Add((slot, stack));

                if (!player.InventoryManager.TryGiveItemstack(stack, true))
                {
                    player.Entity?.World?.SpawnItemEntity(stack, player.Entity.Pos.XYZ);
                }
            }

            if (emptied.Count == 0) return 0;

            GearSlotOrderCompat.MarkDirtyAfterClear(emptied);
            log?.Notification("[canevents] Recovered {0} item(s) from disabled character slots of {1}.",
                emptied.Count, player.PlayerName);
            return emptied.Count;
        }
    }
}
