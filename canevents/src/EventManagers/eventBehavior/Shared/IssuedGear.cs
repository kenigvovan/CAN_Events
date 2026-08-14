using System;
using Vintagestory.API.Common;

namespace canevents.src.EventManagers.eventBehavior.Shared
{
    /// <summary>What happens to match-issued kit the moment its holder throws it on the ground.</summary>
    public enum IssuedGearDrop
    {
        /// <summary>Left alone - what the mod always did.</summary>
        Allow,
        /// <summary>Handed straight back to whoever dropped it.</summary>
        Return,
        /// <summary>Destroyed on the spot.</summary>
        Remove,
        /// <summary>Dropping works as usual; marked items are swept off the arena when the match ends.</summary>
        Cleanup
    }

    /// <summary>
    /// The mark every issued kit item carries, and the policy that decides what happens when one is
    /// thrown away.
    ///
    /// The mark is the only thing separating a match sword from a player's own belongings, so it is
    /// also the safety rail: nothing unmarked is ever touched by the policy. Rewards, entry-fee
    /// refunds, Hunger Games loot and sponsor drops carry no mark and go on behaving exactly as
    /// before.
    /// </summary>
    public static class IssuedGear
    {
        public const string LoadoutKey = "canevents_loadout";
        public const string IssuedToKey = "canevents_issuedTo";

        /// <summary>Marks a piece of kit that SHOULD hit the ground when its owner dies, against the
        /// usual rule that issued gear is swept up before the body drops it. For a mode where the
        /// weapon is the story - a dead sheriff's bow lying there for anyone to pick up.</summary>
        public const string DropOnDeathKey = "canevents_dropOnDeath";

        /// <summary>Used when a policy string is missing or unreadable. Dropping keeps working, the
        /// arena just does not accumulate abandoned kit.</summary>
        public const IssuedGearDrop DefaultPolicy = IssuedGearDrop.Cleanup;

        /// <summary>Stamps a stack as issued for a match. <paramref name="issuedTo"/> is a display
        /// name (a bot has no player object), read only by logs.</summary>
        public static void Mark(ItemStack stack, string issuedTo)
        {
            if (stack?.Attributes == null) return;
            stack.Attributes.SetBool(LoadoutKey, true);
            if (issuedTo != null) stack.Attributes.SetString(IssuedToKey, issuedTo);
        }

        public static bool IsIssued(ItemStack stack)
            => stack?.Attributes?.GetBool(LoadoutKey, false) == true;

        /// <summary>Who it was handed to, for log lines. Null when the stack is not issued gear.</summary>
        public static string IssuedTo(ItemStack stack)
            => stack?.Attributes?.GetString(IssuedToKey);

        /// <summary>Lets this stack fall to the ground on its owner's death instead of being swept up.</summary>
        public static void MarkDropOnDeath(ItemStack stack)
            => stack?.Attributes?.SetBool(DropOnDeathKey, true);

        public static bool DropsOnDeath(ItemStack stack)
            => stack?.Attributes?.GetBool(DropOnDeathKey, false) == true;

        /// <summary>
        /// Reads the server-wide default. An unreadable value falls back and says so once - the
        /// admin cannot fix what they are never told about, and throwing here would take the whole
        /// server down over a typo.
        /// </summary>
        public static IssuedGearDrop ParseDefault(string value, Log log)
        {
            if (TryParse(value, out var policy)) return policy;
            if (!string.IsNullOrWhiteSpace(value))
                log?.Warning("issuedGearDrop '{0}' is not one of return/remove/cleanup/allow; using {1}.",
                    value, NameOf(DefaultPolicy));
            return DefaultPolicy;
        }

        /// <summary>
        /// Reads a per-event override. Absent means "no opinion" and is silent: most events set
        /// nothing and inherit the server default.
        /// </summary>
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
