using System;
using System.Collections.Generic;
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

        /// <summary>Dresses a player. Only fills clothing slots that are still empty.</summary>
        public static void Dress(IServerPlayer player, ICoreServerAPI sapi, List<string[]> outfits)
        {
            if (player == null) return;
            var inv = player.InventoryManager.GetOwnInventory(GlobalConstants.characterInvClassName);
            Dress(inv, sapi, outfits, player.PlayerUID, player.PlayerName);
        }

        /// <summary>Dresses a bot body through its seraph gear inventory (same slot layout).</summary>
        public static void Dress(Entity entity, ICoreServerAPI sapi, List<string[]> outfits, string uid)
        {
            var inv = entity?.GetBehavior<EntityBehaviorSeraphInventory>()?.Inventory;
            Dress(inv, sapi, outfits, uid, entity?.GetName());
        }

        private static void Dress(IInventory inv, ICoreServerAPI sapi, List<string[]> outfits, string uid, string who)
        {
            if (inv == null || outfits == null || outfits.Count == 0 || uid == null) return;

            foreach (string code in outfits[StableIndex(uid, outfits.Count)])
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

                slot.Itemstack = new ItemStack(item);
                slot.MarkDirty();
            }

            sapi.World.Logger.Debug("[outfit] Dressed {0}", who ?? uid);
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
