using System;
using System.Collections.Generic;
using Vintagestory.API.Common;

namespace canevents.src.Compat
{
    /// <summary>
    /// Interop with CombatOverhaul's ArmorInventory, whose gear slots form a two-level parent/child
    /// hierarchy: a "waistgear" belt enables the "addBelt*" slots hanging on it (sheaths, quivers,
    /// pouches from QuiversAndSheaths), and "backgear" enables "addBackpack1-4". Nothing here touches
    /// the CO assembly or reflection: which slot an item goes into is written in its own vanilla JSON
    /// attributes (clothescategory / clothescategories - the exact source CO's GearSlot.IsGearType
    /// reads), so both sides of the parent/child relation can be read off the stacks themselves. The
    /// addBelt*-to-waistgear and addBackpack*-to-backgear pairing mirrors CO's GearSlot.SetParentSlot,
    /// which hardcodes the same tables.
    ///
    /// Why the order of MarkDirty calls is life-and-death: CO reacts to a modified gear slot by
    /// force-dropping the contents of every dependent slot whose parent is missing into the SINGLE
    /// ground-inventory slot - and on any failure (second orphan, occupied slot, re-entrant TakeOut)
    /// it marks the still-full slot dirty again and recurses until the process dies of an uncatchable
    /// StackOverflowException. The client applies inventory packets one at a time in send order, so
    /// the server must never announce a state in which a dependent slot is filled while its parent is
    /// absent: when stripping gear, dependents go first; when handing gear back, parents go first,
    /// and a dependent whose parent never made it back must not be announced at all.
    /// </summary>
    public static class GearSlotOrderCompat
    {
        /// <summary>True if wearing this stack ENABLES other gear slots (a belt, a back sling):
        /// it must be announced before anything that hangs on it.</summary>
        public static bool IsSlotEnabler(ItemStack stack)
            => ParentKindOf(CategoriesOf(stack)) == null && HasCategory(stack, "waistgear", "backgear");

        /// <summary>True if this stack lives in a slot that only exists while a parent item is worn
        /// (an addBelt* / addBackpack* gear slot).</summary>
        public static bool IsDependentGear(ItemStack stack)
            => ParentKindOf(CategoriesOf(stack)) != null;

        /// <summary>Notifies emptied slots dependents-first, so the client never sees a belt vanish
        /// while a sheath still hangs on it. Takes (slot, removed stack) pairs because by the time
        /// anything is announced the slots are already empty - the classification must come from what
        /// WAS there.</summary>
        public static void MarkDirtyAfterClear(List<(ItemSlot slot, ItemStack removed)> emptied)
        {
            foreach (var (slot, removed) in emptied) if (IsDependentGear(removed)) SafeMark(slot);
            foreach (var (slot, removed) in emptied) if (!IsDependentGear(removed)) SafeMark(slot);
        }

        // One slot's listeners throwing must not leave the remaining slots unannounced (a silent
        // desync). Cannot help against CO's recursion - a StackOverflowException is uncatchable -
        // which is why everything else in this class exists.
        private static void SafeMark(ItemSlot slot)
        {
            try { slot.MarkDirty(); }
            catch (Exception) { }
        }

        /// <summary>
        /// Notifies filled slots parents-first. A dependent whose parent item is nowhere in the same
        /// inventory by the time its turn comes (the belt failed to restore: unresolvable item,
        /// occupied slot) is never announced - that is the fatal case described above. Its stack is
        /// quietly taken back out (the fill was silent, nothing has reacted to it yet) and handed to
        /// <paramref name="rescue"/> to reach the player some ordinary way instead.
        /// </summary>
        public static void MarkDirtyAfterFill(List<ItemSlot> filledSlots, Action<ItemStack> rescue)
        {
            foreach (var slot in filledSlots)
                if (!IsDependentGear(slot.Itemstack)) SafeMark(slot);

            foreach (var slot in filledSlots)
            {
                var stack = slot.Itemstack;
                string parentKind = ParentKindOf(CategoriesOf(stack));
                if (parentKind == null) continue; // announced above

                if (InventoryHoldsCategory(slot.Inventory, parentKind))
                {
                    SafeMark(slot);
                }
                else
                {
                    slot.Itemstack = null;
                    rescue(stack);
                }
            }
        }

        // The category of the parent slot a dependent category hangs on - CO's
        // GearSlot.SetParentSlot hardcodes exactly these two families.
        private static string ParentKindOf(IEnumerable<string> categories)
        {
            foreach (var cat in categories)
            {
                if (cat.StartsWith("addBelt", StringComparison.OrdinalIgnoreCase)) return "waistgear";
                if (cat.StartsWith("addBackpack", StringComparison.OrdinalIgnoreCase)) return "backgear";
            }
            return null;
        }

        // Which gear slot(s) an item goes into, from the same attributes CO's IsGearType reads.
        private static IEnumerable<string> CategoriesOf(ItemStack stack)
        {
            var attributes = stack?.Collectible?.Attributes;
            if (attributes == null) yield break;

            string single = attributes["clothescategory"].AsString()
                ?? attributes["attachableToEntity"]["categoryCode"].AsString();
            if (single != null) yield return single;

            string[] multiple = attributes["clothescategories"].AsObject<string[]>()
                ?? attributes["attachableToEntity"]["categoryCodes"].AsObject<string[]>();
            if (multiple != null)
                foreach (var cat in multiple)
                    if (cat != null) yield return cat;
        }

        private static bool HasCategory(ItemStack stack, params string[] wanted)
        {
            foreach (var cat in CategoriesOf(stack))
                foreach (var w in wanted)
                    if (string.Equals(cat, w, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static bool InventoryHoldsCategory(IInventory inv, string category)
        {
            if (inv == null) return false;
            for (int i = 0; i < inv.Count; i++)
                if (HasCategory(inv[i]?.Itemstack, category)) return true;
            return false;
        }
    }
}
