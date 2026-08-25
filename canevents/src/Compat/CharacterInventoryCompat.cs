using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.Common;
using Vintagestory.GameContent;

namespace canevents.src.Compat
{
    /// <summary>
    /// Puts worn gear on without assuming the character inventory is vanilla's.
    ///
    /// Combat Overhaul (and its forks - the modid varies, "combatoverhaul", "combatoverhaulfork",
    /// OverhaullibLegacy, so checking for it by name is not a test that holds) replaces the character
    /// inventory with its own ArmorInventory. There the three vanilla armour slots
    /// (EnumCharacterDressType.ArmorHead/Body/Legs, indices 12-14) still exist but are switched off:
    /// DrawUnavailable, and CanHold refuses everything. Seating a stack there by index - which is what
    /// this mod used to do - bypasses CanHold and produces a state the game cannot survive: vanilla's
    /// ModSystemWearableStats.handleDamaged reads inventory slots 12-14 on EVERY hit, and for armour
    /// CO has taken over, its WearableArmorBehavior answers IsArmorType() = true but
    /// GetProtectionModifiers() = null (CO strips the vanilla protectionModifiers). The resulting
    /// NullReferenceException surfaces inside the attacker's melee packet, and the server kicks the
    /// attacker with "Threw an exception at the server". The same stuck stack also hangs inventory
    /// sync on teleport.
    ///
    /// So: never write by slot index, always let the slot say whether it accepts the piece.
    /// </summary>
    public static class CharacterInventoryCompat
    {
        /// <summary>
        /// A slot that is drawn as unavailable is one the inventory has switched off - CO's leftover
        /// vanilla armour slots, and anything else a mod disables the same way. Nothing may be seated
        /// there, no matter what CanHold says.
        /// </summary>
        public static bool IsUsable(ItemSlot slot) => slot != null && !slot.DrawUnavailable;

        /// <summary>
        /// The slot that would take <paramref name="stack"/>, or null if none will. Changes nothing -
        /// callers that must decide before committing (an outfit only conjures a piece once it knows
        /// there is somewhere to put it) ask here first.
        /// </summary>
        /// <param name="dressType">
        /// The vanilla clothes category of the stack, when it is known. Used only as a last resort on
        /// an unmodified inventory: vanilla's ItemSlotCharacter.CanHold reads "clothescategory" alone,
        /// so a piece whose category comes from "clothesCategoryByType" would be refused by every slot
        /// even though vanilla itself routes it by index.
        /// </param>
        /// <param name="emptyOnly">
        /// Refuses to displace a piece that is already worn. Outfits use this - the loadout's armour
        /// went on first and balances the fight - while loadouts themselves allow the overwrite, so a
        /// multi-layer set collapses to "the last layer in the JSON is the one worn".
        /// </param>
        public static ItemSlot FindWearSlot(IInventory inv, ItemStack stack,
            EnumCharacterDressType? dressType = null, bool emptyOnly = false)
        {
            if (inv == null || stack == null) return null;

            var dummy = new DummySlot(stack);

            // First pass: an empty slot that takes the piece.
            for (int i = 0; i < inv.Count; i++)
            {
                var slot = inv[i];
                if (!IsUsable(slot) || slot.Itemstack != null) continue;
                if (slot.CanHold(dummy)) return slot;
            }

            // Second pass: the last matching slot, occupied or not.
            if (!emptyOnly)
            {
                for (int i = inv.Count - 1; i >= 0; i--)
                {
                    var slot = inv[i];
                    if (IsUsable(slot) && slot.CanHold(dummy)) return slot;
                }
            }

            // Vanilla-only fallback, for the byType category case described above. Still gated on
            // IsUsable, so it can never reach a switched-off slot.
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

        /// <summary>
        /// Seats <paramref name="stack"/> in the slot that accepts it and returns that slot, or leaves
        /// the inventory untouched and returns null. Does NOT MarkDirty: with CO's gear hierarchy the
        /// order of notifications matters, so callers that seat several pieces collect the filled slots
        /// and hand them to <see cref="GearSlotOrderCompat.MarkDirtyAfterFill"/> once all are in.
        /// </summary>
        public static ItemSlot Wear(IInventory inv, ItemStack stack,
            EnumCharacterDressType? dressType = null, bool emptyOnly = false)
        {
            var slot = FindWearSlot(inv, stack, dressType, emptyOnly);
            if (slot != null) slot.Itemstack = stack;
            return slot;
        }

        /// <summary>True while the character inventory is the unmodified vanilla one. An exact type
        /// match on purpose: a subclass is somebody's replacement and gets the careful path.</summary>
        public static bool IsVanilla(IInventory inv)
            => inv != null && inv.GetType() == typeof(InventoryCharacter);

        /// <summary>
        /// Empties every switched-off character slot, handing what was in it back to the player (or
        /// dropping it at their feet if there is no room). Runs on join because a piece put there by
        /// an older version of this mod stays there across restarts and crashes the server on every
        /// hit the player takes - the player cannot even take it off, the slot is not drawn.
        /// Returns how many pieces were recovered.
        /// </summary>
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
