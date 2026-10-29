using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers.events;
using canevents.src.EventPlacesManagers.Places;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace canevents.src.EventManagers.eventBehavior.Shared
{
    // HG loot chests on marked spots. They overwrite world blocks, so originals are journaled and restored.
    public sealed class HungerGamesLoot
    {
        private sealed class LootEntry
        {
            public string Code;
            public int MinQty = 1;
            public int MaxQty = 1;
            public float Weight = 1f;
        }

        private readonly Dictionary<string, List<LootEntry>> tables = new Dictionary<string, List<LootEntry>>();

        // Chests this match placed. The feast refills only these, never a player chest.
        private readonly HashSet<string> placedChestKeys = new HashSet<string>();

        // For manual cleanup when the rollback file is missing.
        private readonly List<BlockPos> placedChestPositions = new List<BlockPos>();

        // Only blocks still matching this id get removed.
        private int placedChestBlockId;

        private string containerBlockCode = "game:chest-east";
        private string defaultTier = "common";
        private int minSlotsPerChest = 3;
        private int maxSlotsPerChest = 6;

        public void Parse(JsonObject attributes)
        {
            tables.Clear();
            containerBlockCode = attributes["lootContainerBlock"].AsString("game:chest-east");
            defaultTier = attributes["defaultLootTier"].AsString("common");
            minSlotsPerChest = attributes["minSlotsPerChest"].AsInt(3);
            maxSlotsPerChest = attributes["maxSlotsPerChest"].AsInt(6);

            // { "common": [...], "rare": [...], "cornucopia": [...] }
            var tablesAttr = attributes["lootTables"];
            if (tablesAttr == null || !(tablesAttr.Token is Newtonsoft.Json.Linq.JObject jo)) return;

            foreach (var prop in jo.Properties())
            {
                if (!(prop.Value is Newtonsoft.Json.Linq.JArray arr)) continue;
                var entries = new List<LootEntry>();
                foreach (var item in arr)
                {
                    var entry = new LootEntry
                    {
                        Code = item.Value<string>("code"),
                        MinQty = item["minQty"]?.ToObject<int>() ?? 1,
                        MaxQty = item["maxQty"]?.ToObject<int>() ?? 1,
                        Weight = item["weight"]?.ToObject<float>() ?? 1f
                    };
                    // rng.Next(min, max+1) throws if min > max.
                    if (entry.MaxQty < entry.MinQty) entry.MaxQty = entry.MinQty;
                    entries.Add(entry);
                }
                if (entries.Count > 0) tables[prop.Name] = entries;
            }
        }

        public void Place(RunningEvent runningEvent)
        {
            if (tables.Count == 0) return;

            var place = runningEvent.Place;
            if (place == null || !place.TryGetBehaviorData(BehaviorKeys.HungerGames, out var data)) return;
            if (!(data["LootSpots"] is Newtonsoft.Json.Linq.JArray spots)) return;

            var world = runningEvent.sapi.World;
            var ba = world.BlockAccessor;
            var chestBlock = world.GetBlock(new AssetLocation(containerBlockCode));
            if (chestBlock == null || chestBlock.BlockId == 0)
            {
                world.Logger.Error("[HG] Loot container block '{0}' not found. Skipping chest placement.", containerBlockCode);
                return;
            }

            // Snapshot first. Spots with a BE are skipped: SetBlock would delete its contents without a drop.
            var toPlace = new List<(BlockPos pos, string tier)>();
            var snapshot = new Dictionary<BlockPos, int>();
            // Leftover chests of ours, cleared to air before placing.
            var stale = new HashSet<string>();
            foreach (var spot in spots)
            {
                if (!JsonPos.TryRead(spot, out var pos)) continue;
                string tier = spot["Tier"]?.ToObject<string>() ?? defaultTier;
                if (!tables.ContainsKey(tier))
                {
                    world.Logger.Warning("[HG] LootSpot at {0} references unknown tier '{1}', falling back to '{2}'.", pos, tier, defaultTier);
                    tier = defaultTier;
                    if (!tables.ContainsKey(tier)) continue;
                }

                if (ba.GetBlockEntity(pos) != null)
                {
                    // Our chest block here is a leftover from a lost rollback; replace it or the spot stays stuck forever.
                    if (ba.GetBlock(pos).BlockId != chestBlock.BlockId)
                    {
                        world.Logger.Warning("[HG] LootSpot at {0} sits on an existing block entity (chest/sign/...); "
                            + "skipping it so its contents aren't destroyed. Move the spot onto an empty block.", pos);
                        continue;
                    }

                    world.Logger.Warning("[HG] LootSpot at {0} still holds a loot chest from an earlier match - "
                        + "replacing it. What was under it is no longer recorded, so this spot rolls back to air.", pos);
                    stale.Add(ArenaBlockJournal.Key(pos));
                    snapshot[pos.Copy()] = 0;
                    toPlace.Add((pos, tier));
                    continue;
                }

                snapshot[pos.Copy()] = ba.GetBlock(pos).BlockId;
                toPlace.Add((pos, tier));
            }

            if (toPlace.Count == 0) return;

            // Journal before any SetBlock, so a crash can't leave chests with no record.
            if (!ArenaBlockJournal.Record(place.Id, ArenaBlockJournal.TagHgLoot, snapshot))
            {
                world.Logger.Error("[HG] could not write loot rollback for place {0}; not placing chests.", place.Id);
                return;
            }

            placedChestKeys.Clear();
            placedChestPositions.Clear();
            placedChestBlockId = chestBlock.BlockId;
            foreach (var (pos, tier) in toPlace)
            {
                string key = ArenaBlockJournal.Key(pos);
                // SetBlock with the same id keeps the old BE and its loot; air first resets it.
                if (stale.Contains(key)) ba.SetBlock(0, pos);
                ba.SetBlock(chestBlock.BlockId, pos);
                FillChest(runningEvent, pos, tier);
                placedChestKeys.Add(key);
                placedChestPositions.Add(pos.Copy());
            }
            ba.Commit();
            world.Logger.Notification("[HG] Placed {0} loot chests on place id={1}.", toPlace.Count, place.Id);
        }

        // Used for sponsor gifts.
        public ItemStack RollItem(RunningEvent runningEvent, string tier)
        {
            if (tier == null || !tables.TryGetValue(tier, out var table)) return null;
            float weightSum = 0f;
            for (int i = 0; i < table.Count; i++) weightSum += table[i].Weight;
            if (weightSum <= 0f) return null;

            var world = runningEvent.sapi.World;
            var entry = PickWeighted(table, weightSum, (float)world.Rand.NextDouble());
            int qty = world.Rand.Next(entry.MinQty, entry.MaxQty + 1);
            if (qty <= 0) return null;

            return ItemLookup.StackOf(world, entry.Code, qty);
        }

        // Feast refill, free slots only. Inventory-only, so nothing to journal.
        public int RefillTier(RunningEvent runningEvent, string tier)
        {
            if (tier == null || !tables.ContainsKey(tier)) return 0;
            var place = runningEvent.Place;
            if (place == null || !place.TryGetBehaviorData(BehaviorKeys.HungerGames, out var data)) return 0;
            if (!(data["LootSpots"] is Newtonsoft.Json.Linq.JArray spots)) return 0;

            int refilled = 0;
            foreach (var spot in spots)
            {
                string spotTier = spot["Tier"]?.ToObject<string>() ?? defaultTier;
                if (!string.Equals(spotTier, tier, StringComparison.OrdinalIgnoreCase)) continue;
                if (!JsonPos.TryRead(spot, out var pos)) continue;
                if (!placedChestKeys.Contains(ArenaBlockJournal.Key(pos))) continue;
                if (!(runningEvent.sapi.World.BlockAccessor.GetBlockEntity(pos) is BlockEntityContainer)) continue;

                if (FillChest(runningEvent, pos, tier) > 0) refilled++;
            }
            return refilled;
        }

        // Journal is persisted, so a crash mid-match is also rolled back at boot.
        public void Restore(RunningEvent runningEvent)
        {
            var world = runningEvent.sapi.World;
            var place = runningEvent.Place;

            // Fallback when the journal is gone: remove our chests by hand.
            ArenaBlockJournal.RestoreOr(place?.Id, ArenaBlockJournal.TagHgLoot, () =>
            {
                if (placedChestPositions.Count == 0) return;
                world.Logger.Warning("[HG] no loot rollback file for place {0}; removing the {1} chest(s) "
                    + "this match placed by hand.", place?.Id ?? -1, placedChestPositions.Count);

                var ba = world.BlockAccessor;
                foreach (var pos in placedChestPositions)
                    if (ba.GetBlock(pos)?.BlockId == placedChestBlockId) ba.SetBlock(0, pos);
                ba.Commit();
            });

            placedChestKeys.Clear();
            placedChestPositions.Clear();
        }

        // Returns stacks placed.
        private int FillChest(RunningEvent runningEvent, BlockPos pos, string tier)
        {
            var world = runningEvent.sapi.World;
            var rng = world.Rand;
            var be = world.BlockAccessor.GetBlockEntity(pos) as BlockEntityContainer;
            if (be?.Inventory == null) return 0;

            var table = tables[tier];
            float weightSum = 0f;
            for (int i = 0; i < table.Count; i++) weightSum += table[i].Weight;
            if (weightSum <= 0f) return 0;

            // Free slots only: assigning Itemstack destroys what was there.
            var freeSlots = new List<int>();
            int invSize = be.Inventory.Count;
            for (int i = 0; i < invSize; i++)
                if (be.Inventory[i].Empty) freeSlots.Add(i);
            if (freeSlots.Count == 0) return 0;

            int slots = rng.Next(minSlotsPerChest, maxSlotsPerChest + 1);
            if (slots > freeSlots.Count) slots = freeSlots.Count;

            var indices = freeSlots.OrderBy(_ => rng.Next()).Take(slots);
            int placed = 0;
            foreach (int slotIdx in indices)
            {
                var entry = PickWeighted(table, weightSum, (float)rng.NextDouble());
                int qty = rng.Next(entry.MinQty, entry.MaxQty + 1);
                if (qty <= 0) continue;

                var stack = ItemLookup.StackOf(world, entry.Code, qty);
                if (stack == null)
                {
                    world.Logger.Warning("[HG] Loot entry '{0}' not found.", entry.Code);
                    continue;
                }
                be.Inventory[slotIdx].Itemstack = stack;
                be.Inventory[slotIdx].MarkDirty();
                placed++;
            }
            if (placed > 0) be.MarkDirty(true);
            return placed;
        }

        private static LootEntry PickWeighted(List<LootEntry> table, float weightSum, float roll)
        {
            float pick = roll * weightSum;
            float acc = 0f;
            for (int i = 0; i < table.Count; i++)
            {
                acc += table[i].Weight;
                if (pick <= acc) return table[i];
            }
            return table[table.Count - 1];
        }
    }
}
