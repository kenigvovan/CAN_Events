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
    /// <summary>
    /// The loot side of Hunger Games: weighted tables from the event config, chests placed on the
    /// place's marked spots at match start, and the original blocks put back at the end.
    /// Restoring matters: these chests overwrite real world blocks.
    /// </summary>
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

        // Positions where THIS match placed a chest (Store.Key form). The feast refills only these, so
        // a spot Place() skipped because a real player chest sat there is never overwritten.
        private readonly HashSet<string> placedChestKeys = new HashSet<string>();

        // The same positions as blocks, for the hand teardown Restore falls back to when no rollback
        // file survives. Kept alongside the keys rather than parsed back out of them.
        private readonly List<BlockPos> placedChestPositions = new List<BlockPos>();

        // Block id actually placed, resolved in Place(). Only a block that is still ours gets removed.
        private int placedChestBlockId;

        private string containerBlockCode = "game:chest-east";
        private string defaultTier = "common";
        private int minSlotsPerChest = 3;
        private int maxSlotsPerChest = 6;

        /// <summary>Reads loot tables and chest settings from the event's attributes.</summary>
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
                    // min > max would make rng.Next(min, max+1) throw mid-fill; clamp instead.
                    if (entry.MaxQty < entry.MinQty) entry.MaxQty = entry.MinQty;
                    entries.Add(entry);
                }
                if (entries.Count > 0) tables[prop.Name] = entries;
            }
        }

        /// <summary>Places and fills a chest on every loot spot marked on the event's place.</summary>
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

            // Pass 1: resolve spots and snapshot current blocks without touching the world yet. Spots
            // holding a block entity are skipped: SetBlock would delete it and its contents with no drop.
            var toPlace = new List<(BlockPos pos, string tier)>();
            var snapshot = new Dictionary<string, int>();
            // Spots a leftover loot chest of ours occupies. They are cleared to air first, so the new
            // chest is a fresh container rather than the old one topped up.
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
                    // Our own loot container standing here is what an earlier match left behind (its
                    // rollback never ran, or the file was lost). Skipping it would wedge the spot for
                    // good: every later match skips it too, so nothing ever clears it either. Ours is
                    // safe to take out; anything else is somebody's build and is still left alone.
                    if (ba.GetBlock(pos).BlockId != chestBlock.BlockId)
                    {
                        world.Logger.Warning("[HG] LootSpot at {0} sits on an existing block entity (chest/sign/...); "
                            + "skipping it so its contents aren't destroyed. Move the spot onto an empty block.", pos);
                        continue;
                    }

                    world.Logger.Warning("[HG] LootSpot at {0} still holds a loot chest from an earlier match - "
                        + "replacing it. What was under it is no longer recorded, so this spot rolls back to air.", pos);
                    stale.Add(HungerGamesLootStore.Key(pos));
                    snapshot[HungerGamesLootStore.Key(pos)] = 0;
                    toPlace.Add((pos, tier));
                    continue;
                }

                snapshot[HungerGamesLootStore.Key(pos)] = ba.GetBlock(pos).BlockId;
                toPlace.Add((pos, tier));
            }

            if (toPlace.Count == 0) return;

            // Write the rollback file BEFORE any SetBlock: a crash in between would otherwise leave
            // chests standing with no record of what was under them. Saving too early is harmless.
            if (!HungerGamesLootStore.Save(place.Id, snapshot))
            {
                world.Logger.Error("[HG] could not write loot rollback for place {0}; not placing chests.", place.Id);
                return;
            }

            placedChestKeys.Clear();
            placedChestPositions.Clear();
            placedChestBlockId = chestBlock.BlockId;
            foreach (var (pos, tier) in toPlace)
            {
                string key = HungerGamesLootStore.Key(pos);
                // Same block id over itself keeps the existing block entity - and last match's loot
                // with it. Air first forces a new, empty container.
                if (stale.Contains(key)) ba.SetBlock(0, pos);
                ba.SetBlock(chestBlock.BlockId, pos);
                FillChest(runningEvent, pos, tier);
                placedChestKeys.Add(key);
                placedChestPositions.Add(pos.Copy());
            }
            ba.Commit();
            world.Logger.Notification("[HG] Placed {0} loot chests on place id={1}.", toPlace.Count, place.Id);
        }

        /// <summary>
        /// Rolls one random weighted stack from a tier's table (sponsor gifts), or null when the tier
        /// is unknown or the rolled code no longer resolves.
        /// </summary>
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

            var loc = new AssetLocation(entry.Code);
            var item = world.GetItem(loc);
            if (item != null) return new ItemStack(item, qty);
            var block = world.GetBlock(loc);
            return block != null ? new ItemStack(block, qty) : null;
        }

        /// <summary>
        /// The feast: tops up every still-standing chest of the given tier, writing into free slots only
        /// so tributes' stored items survive. Inventory-only, so no rollback bookkeeping is needed.
        /// </summary>
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
                // Only refill chests THIS match placed, never a pre-existing player chest.
                if (!placedChestKeys.Contains(HungerGamesLootStore.Key(pos))) continue;
                if (!(runningEvent.sapi.World.BlockAccessor.GetBlockEntity(pos) is BlockEntityContainer)) continue;

                // A chest with no room left counts as not restocked, so announcements stay truthful.
                if (FillChest(runningEvent, pos, tier) > 0) refilled++;
            }
            return refilled;
        }

        /// <summary>Puts back every block a chest replaced and clears the rollback file. Persisted, so
        /// an unclean shutdown mid-match is swept at boot too.</summary>
        public void Restore(RunningEvent runningEvent)
        {
            var world = runningEvent.sapi.World;
            var place = runningEvent.Place;
            bool restored = place != null && HungerGamesLootStore.Restore(place.Id);

            // No rollback file - lost, or the arena was released before this ran - while this match
            // did place chests. Take them out by hand: a chest left standing wedges its spot for
            // every later match, which then skips it and never clears it either.
            if (!restored && placedChestPositions.Count > 0)
            {
                world.Logger.Warning("[HG] no loot rollback file for place {0}; removing the {1} chest(s) "
                    + "this match placed by hand.", place?.Id ?? -1, placedChestPositions.Count);

                var ba = world.BlockAccessor;
                foreach (var pos in placedChestPositions)
                    if (ba.GetBlock(pos)?.BlockId == placedChestBlockId) ba.SetBlock(0, pos);
                ba.Commit();
            }

            placedChestKeys.Clear();
            placedChestPositions.Clear();
        }

        /// <summary>Rolls loot into a chest and returns how many stacks it actually placed.</summary>
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

            // Only free slots are eligible: assigning Itemstack over an occupied slot destroys the
            // stack outright, with no drop.
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

                var loc = new AssetLocation(entry.Code);
                ItemStack stack = null;
                var item = world.GetItem(loc);
                if (item != null) stack = new ItemStack(item, qty);
                else
                {
                    var block = world.GetBlock(loc);
                    if (block != null) stack = new ItemStack(block, qty);
                }
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
