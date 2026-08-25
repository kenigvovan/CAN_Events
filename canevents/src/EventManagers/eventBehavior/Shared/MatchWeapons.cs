using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace canevents.src.EventManagers.eventBehavior.Shared
{
    /// <summary>
    /// What a match can do to the weapons a player is carrying: hide one, take them all away, or
    /// mark them to survive their owner's death.
    ///
    /// None of it is about any particular mode - Murder Mystery happens to need all three, but
    /// "the weapon in your hand announces you" is true of every hidden-role game, and "this one is
    /// meant to be looted off the body" of any mode where a weapon is the prize.
    /// </summary>
    public static class MatchWeapons
    {
        /// <summary>
        /// Anything that reads as a weapon when someone is holding it. Tool covers the knife and the
        /// bow; AttackPower catches a club or a rock handed out as a kit item.
        /// </summary>
        public static bool IsWeapon(ItemStack stack)
            => stack?.Collectible != null
               && (stack.Collectible.Tool != null || stack.Collectible.AttackPower > 1f);

        /// <summary>
        /// Ammunition. Not a weapon by any measure the game uses - an arrow has no Tool and no attack
        /// power in the hand - but a fistful of arrows names its owner just as loudly as the bow does,
        /// and there is only one role in Murder Mystery that was issued any.
        /// </summary>
        public static bool IsAmmo(ItemStack stack)
        {
            var collectible = stack?.Collectible;
            if (collectible == null) return false;
            if (collectible is ItemArrow) return true;

            // Modded ammo is not an ItemArrow, and the naming is consistent enough to lean on: the
            // cost of a false positive here is an item staying out of sight, which is the default
            // this mode wants anyway.
            var path = collectible.Code?.Path;
            return path != null && (path.Contains("arrow") || path.Contains("bolt"));
        }

        /// <summary>Anything that gives away an armed player: the weapon itself or what feeds it.</summary>
        public static bool IsTelltale(ItemStack stack) => IsWeapon(stack) || IsAmmo(stack);

        /// <summary>
        /// Takes the weapon out of the hand everyone can see. The held item is rendered on the body
        /// for every other player, so a drawn knife names its owner before anything has happened.
        ///
        /// Moved to a slot the player is not currently holding, and the hand is then filled with
        /// something harmless from the same hotbar - an empty hand among full ones is just as much
        /// of a tell. Drawing the weapon becomes the owner's own decision, and the moment it appears
        /// is a real clue rather than a given.
        /// </summary>
        public static void MoveOutOfHand(IServerPlayer player)
        {
            var hotbar = player?.InventoryManager?.GetOwnInventory(GlobalConstants.hotBarInvClassName);
            if (hotbar == null) return;

            // Off-hand and beyond are not part of the visible hotbar row (see LoadoutService).
            int lastSlot = Math.Min(9, hotbar.Count - 1);
            if (lastSlot < 1) return;

            int active = player.InventoryManager.ActiveHotbarSlotNumber;
            if (active < 0 || active > lastSlot) active = 0;

            var handSlot = hotbar[active];
            if (IsTelltale(handSlot?.Itemstack))
            {
                // From the far end: the near slots are the ones people cycle through by habit.
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
                // Ammo counts as a weapon here too, or filling the emptied hand would put the
                // sheriff's arrows on display in place of the bow it just took away.
                if (stack == null || IsTelltale(stack)) continue;
                handSlot.Itemstack = stack;
                hotbar[i].Itemstack = null;
                handSlot.MarkDirty();
                hotbar[i].MarkDirty();
                break;
            }
        }

        /// <summary>
        /// Pulls every weapon out of a player's hands and bags and returns what was taken, so the
        /// caller can hand back those very items later - a half-used bow comes back as it was.
        /// Worn clothing is never touched.
        /// </summary>
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
                    // Ammo goes with it: a disarmed sheriff left holding arrows is disarmed in name
                    // only if the next bow they find comes loaded.
                    if (!IsTelltale(slot?.Itemstack)) continue;
                    taken.Add(slot.Itemstack);
                    slot.Itemstack = null;
                    slot.MarkDirty();
                }
            }
            return taken;
        }

        /// <summary>
        /// Marks this player's weapons to hit the ground when they die, against the usual rule that
        /// issued gear is swept up before the body drops it. WHO gets marked is the mode's decision;
        /// this only carries it out.
        /// </summary>
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
                    // The arrows drop with the bow, or whoever finds the body inherits a weapon they
                    // cannot fire.
                    if (IsTelltale(stack)) IssuedGear.MarkDropOnDeath(stack);
                }
            }
        }
    }
}
