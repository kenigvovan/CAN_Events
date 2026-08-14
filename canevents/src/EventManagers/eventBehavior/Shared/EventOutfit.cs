using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace canevents.src.EventManagers.eventBehavior.Shared
{
    /// <summary>
    /// Plain clothes for everyone in a match - players and bots alike, who both arrive naked
    /// (PlayerInventoryBackup.ClearInventory wipes the character inventory too).
    ///
    /// Clothes, NOT armour: `clothes-*` items carry warmth and no protection, so dressing a fighter
    /// never changes the fight, and only empty clothing slots are filled.
    ///
    /// Configured by the `outfits` attribute (array of arrays of item codes); an empty array disables
    /// it, absent uses the defaults below. Outfit is picked by a stable hash of the uid.
    /// </summary>
    public static class EventOutfit
    {
        private static readonly string[][] DefaultOutfits =
        {
            new[] { "game:clothes-upperbody-peasent-shirt",  "game:clothes-lowerbody-dirty-linen-trousers", "game:clothes-foot-peasent-slippers" },
            new[] { "game:clothes-upperbody-woolen-shirt",   "game:clothes-lowerbody-woolen-leggings",      "game:clothes-foot-worn-leather-boots" },
            new[] { "game:clothes-upperbody-hunter-shirt",   "game:clothes-lowerbody-hunter-leggings",      "game:clothes-foot-hunter-boots" },
            new[] { "game:clothes-upperbody-commoner-shirt", "game:clothes-lowerbody-commoner-trousers",    "game:clothes-foot-commoner-boots" },
            new[] { "game:clothes-upperbody-homespun-shirt", "game:clothes-lowerbody-homespun-pants",       "game:clothes-foot-strawsandals" },
            new[] { "game:clothes-upperbody-linen-tunic",    "game:clothes-lowerbody-nomad-trousers",       "game:clothes-foot-nomad-boots" },
        };

        /// <summary>Reads the `outfits` attribute, falling back to the defaults when absent.</summary>
        public static List<string[]> Parse(JsonObject attributes)
        {
            var configured = attributes?["outfits"]?.AsArray();
            if (configured == null) return new List<string[]>(DefaultOutfits);

            var result = new List<string[]>();
            foreach (var outfit in configured)
            {
                var codes = outfit.AsArray<string>();
                if (codes != null && codes.Length > 0) result.Add(codes);
            }
            return result;   // deliberately can be empty: "outfits": [] means "no clothes"
        }

        /// <summary>
        /// Managed outfits from the outfit store if the event has any, else the ones parsed from
        /// its JSON. Mirrors LoadoutService.GetEffectiveSets: a set becomes one outfit, its item
        /// codes in order, quantities ignored (you wear one of each).
        /// </summary>
        public static List<string[]> GetEffectiveOutfits(string eventCode, LoadoutManager store,
            List<string[]> fallback)
        {
            if (store == null || eventCode == null) return fallback;

            var managed = store.GetSets(eventCode);
            if (managed == null || managed.Count == 0) return fallback;

            var result = new List<string[]>();
            foreach (var set in managed)
            {
                var codes = set.Items?.Select(i => i.Code).Where(c => !string.IsNullOrEmpty(c)).ToArray();
                if (codes != null && codes.Length > 0) result.Add(codes);
            }
            // An outfit store holding nothing but empty sets must not silently undress everyone.
            return result.Count > 0 ? result : fallback;
        }

        /// <summary>
        /// Who wears which outfit, decided once per match.
        ///
        /// <paramref name="stable"/> keeps the historic rule - same uid, same outfit, across matches
        /// and restarts - so regulars stay recognisable. False deals the outfits out as a shuffled
        /// permutation instead, so everyone in one match looks different (wrapping around when there
        /// are more players than outfits). A hidden-role mode needs the latter: with a stable outfit
        /// the colour IS the player, and "the murderer wore red" names them outright.
        ///
        /// Returns null when there is nothing to assign; callers pass -1 per player then.
        /// </summary>
        public static Dictionary<string, int> Assign(IEnumerable<string> uids, int outfitCount,
            bool stable, Random rand = null)
        {
            if (uids == null || outfitCount <= 0) return null;

            var result = new Dictionary<string, int>();
            if (stable)
            {
                foreach (var uid in uids)
                    if (uid != null) result[uid] = StableIndex(uid, outfitCount);
                return result;
            }

            // Shuffled deck, refilled as it runs out: the first outfitCount players are guaranteed
            // distinct, and a bigger lobby repeats in a different order rather than by uid hash.
            var deck = new List<int>();
            foreach (var uid in uids)
            {
                if (uid == null) continue;
                if (deck.Count == 0)
                {
                    for (int i = 0; i < outfitCount; i++) deck.Add(i);
                    for (int i = deck.Count - 1; i > 0; i--)
                    {
                        int j = rand?.Next(i + 1) ?? i;
                        (deck[i], deck[j]) = (deck[j], deck[i]);
                    }
                }
                result[uid] = deck[deck.Count - 1];
                deck.RemoveAt(deck.Count - 1);
            }
            return result;
        }

        /// <summary>Dresses a player. Only fills clothing slots that are still empty.
        /// <paramref name="outfitIndex"/> of -1 falls back to the stable per-uid pick.</summary>
        public static void Dress(IServerPlayer player, ICoreServerAPI sapi, List<string[]> outfits,
            int outfitIndex = -1, bool reclaimCarried = false)
        {
            if (player == null) return;
            var inv = player.InventoryManager.GetOwnInventory(GlobalConstants.characterInvClassName);
            Dress(inv, sapi, outfits, player.PlayerUID, player.PlayerName, outfitIndex,
                reclaimCarried ? player : null);
        }

        /// <summary>Dresses a bot body through its seraph gear inventory (same slot layout).</summary>
        public static void Dress(Entity entity, ICoreServerAPI sapi, List<string[]> outfits, string uid,
            int outfitIndex = -1)
        {
            var inv = entity?.GetBehavior<EntityBehaviorSeraphInventory>()?.Inventory;
            Dress(inv, sapi, outfits, uid, entity?.GetName(), outfitIndex);
        }

        /// <param name="reclaimFrom">
        /// When set, a piece the player took off and is still carrying is put back on instead of a
        /// fresh one being conjured. That is what makes "the outfit stays on" a lock rather than a
        /// duplicator: without it, taking the coat off and keeping it in the backpack leaves the
        /// player wearing a second coat.
        /// </param>
        private static void Dress(IInventory inv, ICoreServerAPI sapi, List<string[]> outfits, string uid,
            string who, int outfitIndex, IServerPlayer reclaimFrom = null)
        {
            if (inv == null || outfits == null || outfits.Count == 0 || uid == null) return;

            int pick = outfitIndex >= 0
                ? outfitIndex % outfits.Count
                : StableIndex(uid, outfits.Count);

            // Every piece is seated first, then notified parents-before-dependents: a gear slot
            // announced while its parent is missing sends CombatOverhaul's client into an uncatchable
            // MarkDirty recursion - see GearSlotOrderCompat.
            var dressed = new List<ItemSlot>();

            foreach (string code in outfits[pick])
            {
                var item = sapi.World.GetItem(new AssetLocation(code));
                if (item == null)
                {
                    sapi.World.Logger.Warning("[outfit] Clothing item not found: {0}", code);
                    continue;
                }

                string category = item.Attributes?["clothescategory"]?.AsString(null);
                if (category == null
                    || !Enum.TryParse<EnumCharacterDressType>(category, true, out var dressType)
                    || (int)dressType < 0 || (int)dressType >= inv.Count)
                {
                    sapi.World.Logger.Warning("[outfit] {0} has no usable clothes category, skipped.", code);
                    continue;
                }

                // Never over an existing piece: the loadout's armour came first and balances the fight.
                var slot = inv[(int)dressType];
                if (slot.Itemstack != null) continue;

                var piece = TakeCarried(reclaimFrom, code);
                if (piece == null)
                {
                    piece = new ItemStack(item);
                    // Marked like a loadout item: an outfit is conjured from thin air too, so a
                    // piece dropped on the arena would otherwise lie there for good - and in a
                    // hidden-role mode someone else's colours are a disguise.
                    IssuedGear.Mark(piece, who ?? uid);
                }
                slot.Itemstack = piece;
                dressed.Add(slot);
            }

            // Outfit pieces are conjured from thin air, so one whose parent slot item is missing is
            // simply not worn - nothing to hand back.
            Compat.GearSlotOrderCompat.MarkDirtyAfterFill(dressed, stack =>
                sapi.World.Logger.Debug("[outfit] {0} skipped: its parent gear is not worn.", stack.Collectible?.Code));

            // Only when something actually went on: Dress is also called on a timer by modes that
            // keep an outfit on, and a line per player per tick would bury the log.
            if (dressed.Count > 0) sapi.World.Logger.Debug("[outfit] Dressed {0}", who ?? uid);
        }

        /// <summary>
        /// Pulls an outfit piece the player is carrying back out of their bags, so it can go back on
        /// instead of a copy being made. Only ever takes a piece this mod issued: a coat of their own
        /// that happens to match the code is theirs, not part of the match wardrobe.
        /// </summary>
        private static ItemStack TakeCarried(IServerPlayer player, string code)
        {
            if (player == null || code == null) return null;

            foreach (var inv in player.InventoryManager.Inventories.Values)
            {
                if (inv.ClassName == "creative") continue;
                // The character inventory is where it is going; anything already there is worn.
                if (inv.ClassName == GlobalConstants.characterInvClassName) continue;

                for (int i = 0; i < inv.Count; i++)
                {
                    var slot = inv[i];
                    var stack = slot?.Itemstack;
                    if (stack?.Collectible?.Code == null) continue;
                    if (!IssuedGear.IsIssued(stack)) continue;
                    if (stack.Collectible.Code.ToString() != code) continue;

                    // One piece is worn at a time; the rest of the stack stays where it is.
                    var piece = stack.Clone();
                    piece.StackSize = 1;
                    stack.StackSize--;
                    if (stack.StackSize <= 0) slot.Itemstack = null;
                    slot.MarkDirty();
                    return piece;
                }
            }
            return null;
        }

        // Same uid -> same outfit, across matches and server restarts. string.GetHashCode is
        // randomised per process in .NET Core, so it cannot be used here.
        private static int StableIndex(string uid, int count)
        {
            int hash = 17;
            foreach (char c in uid) hash = unchecked(hash * 31 + c);
            return Math.Abs(hash % count);
        }
    }
}
