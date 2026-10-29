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
    // Plain clothes (no armour) for players and bots, who arrive naked after ClearInventory.
    // `outfits` attribute: array of item-code arrays; [] disables, absent uses the defaults.
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

        public static List<string[]> Parse(JsonObject attributes)
        {
            var configured = attributes?[BehaviorConfigKeys.Outfits]?.AsArray();
            if (configured == null) return new List<string[]>(DefaultOutfits);

            var result = new List<string[]>();
            foreach (var outfit in configured)
            {
                var codes = outfit.AsArray<string>();
                if (codes != null && codes.Length > 0) result.Add(codes);
            }
            return result;   // empty means no clothes
        }

        // Like LoadoutService.GetEffectiveSets: one set = one outfit, quantities ignored.
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
            return result.Count > 0 ? result : fallback;
        }

        // stable: same uid -> same outfit across matches. Otherwise shuffled per match (hidden-role modes).
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

            // Shuffled deck, refilled when empty.
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

        // outfitIndex -1 = stable per-uid pick.
        public static void Dress(IServerPlayer player, ICoreServerAPI sapi, List<string[]> outfits,
            int outfitIndex = -1, bool reclaimCarried = false)
        {
            if (player == null) return;
            var inv = player.InventoryManager.GetOwnInventory(GlobalConstants.characterInvClassName);
            Dress(inv, sapi, outfits, player.PlayerUID, player.PlayerName, outfitIndex,
                reclaimCarried ? player : null);
        }

        // Bots: seraph gear inventory, same slot layout.
        public static void Dress(Entity entity, ICoreServerAPI sapi, List<string[]> outfits, string uid,
            int outfitIndex = -1)
        {
            var inv = entity?.GetBehavior<EntityBehaviorSeraphInventory>()?.Inventory;
            Dress(inv, sapi, outfits, uid, entity?.GetName(), outfitIndex);
        }

        // reclaimFrom: re-wear a carried piece instead of conjuring a duplicate.
        private static void Dress(IInventory inv, ICoreServerAPI sapi, List<string[]> outfits, string uid,
            string who, int outfitIndex, IServerPlayer reclaimFrom = null)
        {
            if (inv == null || outfits == null || outfits.Count == 0 || uid == null) return;

            int pick = outfitIndex >= 0
                ? outfitIndex % outfits.Count
                : StableIndex(uid, outfits.Count);

            // MarkDirty after all slots are filled, parents first (CombatOverhaul, see GearSlotOrderCompat).
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
                if (category == null)
                {
                    sapi.World.Logger.Warning("[outfit] {0} has no usable clothes category, skipped.", code);
                    continue;
                }
                EnumCharacterDressType? dressType =
                    Enum.TryParse<EnumCharacterDressType>(category, true, out var parsed)
                        ? parsed
                        : (EnumCharacterDressType?)null;

                // By CanHold, not dress-type index: CO disables some slots. See CharacterInventoryCompat.
                var slot = Compat.CharacterInventoryCompat.FindWearSlot(
                    inv, new ItemStack(item), dressType, emptyOnly: true);
                if (slot == null) continue;

                var piece = TakeCarried(reclaimFrom, code);
                if (piece == null)
                {
                    piece = new ItemStack(item);
                    IssuedGear.Mark(piece, who ?? uid);
                }
                slot.Itemstack = piece;
                dressed.Add(slot);
            }

            Compat.GearSlotOrderCompat.MarkDirtyAfterFill(dressed, stack =>
                sapi.World.Logger.Debug("[outfit] {0} skipped: its parent gear is not worn.", stack.Collectible?.Code));

            // Some modes call Dress on a timer.
            if (dressed.Count > 0) sapi.World.Logger.Debug("[outfit] Dressed {0}", who ?? uid);
        }

        // Only issued pieces; the player's own matching coat is left alone.
        private static ItemStack TakeCarried(IServerPlayer player, string code)
        {
            if (player == null || code == null) return null;

            foreach (var inv in player.InventoryManager.Inventories.Values)
            {
                if (inv.ClassName == "creative") continue;
                if (inv.ClassName == GlobalConstants.characterInvClassName) continue;

                for (int i = 0; i < inv.Count; i++)
                {
                    var slot = inv[i];
                    var stack = slot?.Itemstack;
                    if (stack?.Collectible?.Code == null) continue;
                    if (!IssuedGear.IsIssued(stack)) continue;
                    if (stack.Collectible.Code.ToString() != code) continue;

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

        // string.GetHashCode is randomised per process in .NET Core.
        private static int StableIndex(string uid, int count)
        {
            int hash = 17;
            foreach (char c in uid) hash = unchecked(hash * 31 + c);
            return Math.Abs(hash % count);
        }
    }
}
