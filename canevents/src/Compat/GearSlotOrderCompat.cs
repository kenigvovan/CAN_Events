using System;
using System.Collections.Generic;
using Vintagestory.API.Common;

namespace canevents.src.Compat
{
    // CombatOverhaul gear slots: waistgear enables addBelt*, backgear enables addBackpack*
    // (same tables as CO's GearSlot.SetParentSlot). If the client sees a dependent slot filled with no
    // parent, CO recurses into an uncatchable StackOverflowException. So: clear dependents first, fill parents first.
    public static class GearSlotOrderCompat
    {
        public static bool IsSlotEnabler(ItemStack stack)
            => ParentKindOf(CategoriesOf(stack)) == null && HasCategory(stack, "waistgear", "backgear");

        public static bool IsDependentGear(ItemStack stack)
            => ParentKindOf(CategoriesOf(stack)) != null;

        // Slots are already empty here, so classify by the removed stack.
        public static void MarkDirtyAfterClear(List<(ItemSlot slot, ItemStack removed)> emptied)
        {
            foreach (var (slot, removed) in emptied) if (IsDependentGear(removed)) SafeMark(slot);
            foreach (var (slot, removed) in emptied) if (!IsDependentGear(removed)) SafeMark(slot);
        }

        // Keeps one throwing listener from leaving the rest unannounced. Can't catch CO's stack overflow.
        private static void SafeMark(ItemSlot slot)
        {
            try { slot.MarkDirty(); }
            catch (Exception) { }
        }

        // A dependent whose parent didn't come back is taken out silently and passed to rescue.
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

        // For diagnostics: "dependent:waistgear", "enabler:waistgear", or null.
        public static string RoleOf(ItemStack stack)
        {
            string parent = ParentKindOf(CategoriesOf(stack));
            if (parent != null) return "dependent:" + parent;
            if (HasCategory(stack, "waistgear")) return "enabler:waistgear";
            if (HasCategory(stack, "backgear")) return "enabler:backgear";
            return null;
        }

        // Mirrors CO's GearSlot.SetParentSlot.
        private static string ParentKindOf(IEnumerable<string> categories)
        {
            foreach (var cat in categories)
            {
                if (cat.StartsWith("addBelt", StringComparison.OrdinalIgnoreCase)) return "waistgear";
                if (cat.StartsWith("addBackpack", StringComparison.OrdinalIgnoreCase)) return "backgear";
            }
            return null;
        }

        // Same attributes CO's GearSlot.IsGearType reads.
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
