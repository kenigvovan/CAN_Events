using System;
using Vintagestory.API.Common;

namespace canevents.src.EventManagers.eventBehavior.Shared
{
    // What happens when a player throws issued kit on the ground.
    public enum IssuedGearDrop
    {
        Allow,
        // Given back to whoever dropped it.
        Return,
        Remove,
        // Swept off the arena at match end.
        Cleanup
    }

    // Issued-kit marking and drop policy. Unmarked items (own gear, rewards, HG loot) are never touched.
    public static class IssuedGear
    {
        public const string LoadoutKey = "canevents_loadout";
        public const string IssuedToKey = "canevents_issuedTo";

        // Issued item that drops on death instead of being swept up (e.g. the sheriff's bow).
        public const string DropOnDeathKey = "canevents_dropOnDeath";

        public const IssuedGearDrop DefaultPolicy = IssuedGearDrop.Cleanup;

        // issuedTo is a display name (bots have no player), only for logs.
        public static void Mark(ItemStack stack, string issuedTo)
        {
            if (stack?.Attributes == null) return;
            stack.Attributes.SetBool(LoadoutKey, true);
            if (issuedTo != null) stack.Attributes.SetString(IssuedToKey, issuedTo);
        }

        public static bool IsIssued(ItemStack stack)
            => stack?.Attributes?.GetBool(LoadoutKey, false) == true;

        public static string IssuedTo(ItemStack stack)
            => stack?.Attributes?.GetString(IssuedToKey);

        public static void MarkDropOnDeath(ItemStack stack)
            => stack?.Attributes?.SetBool(DropOnDeathKey, true);

        public static bool DropsOnDeath(ItemStack stack)
            => stack?.Attributes?.GetBool(DropOnDeathKey, false) == true;

        // Bad value warns and falls back, never throws.
        public static IssuedGearDrop ParseDefault(string value, Log log)
        {
            if (TryParse(value, out var policy)) return policy;
            if (!string.IsNullOrWhiteSpace(value))
                log?.Warning("issuedGearDrop '{0}' is not one of return/remove/cleanup/allow; using {1}.",
                    value, NameOf(DefaultPolicy));
            return DefaultPolicy;
        }

        // Per-event override; null inherits the server default.
        public static IssuedGearDrop? ParseOverride(string value, Log log)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (TryParse(value, out var policy)) return policy;
            log?.Warning("issuedGearDrop '{0}' is not one of return/remove/cleanup/allow; "
                + "this event falls back to the server default.", value);
            return null;
        }

        public static string NameOf(IssuedGearDrop policy) => policy switch
        {
            IssuedGearDrop.Return => "return",
            IssuedGearDrop.Remove => "remove",
            IssuedGearDrop.Cleanup => "cleanup",
            _ => "allow"
        };

        private static bool TryParse(string value, out IssuedGearDrop policy)
        {
            policy = DefaultPolicy;
            if (string.IsNullOrWhiteSpace(value)) return false;

            var trimmed = value.Trim();
            if (Eq(trimmed, "allow")) { policy = IssuedGearDrop.Allow; return true; }
            if (Eq(trimmed, "return")) { policy = IssuedGearDrop.Return; return true; }
            if (Eq(trimmed, "remove")) { policy = IssuedGearDrop.Remove; return true; }
            if (Eq(trimmed, "cleanup")) { policy = IssuedGearDrop.Cleanup; return true; }
            return false;
        }

        private static bool Eq(string a, string b)
            => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
