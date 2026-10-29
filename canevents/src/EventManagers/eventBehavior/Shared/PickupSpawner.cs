using System;
using System.Collections.Generic;
using canevents.src.EventManagers.events;
using canevents.src.EventPlacesManagers.Places;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace canevents.src.EventManagers.eventBehavior.Shared
{
    // Drops inert tokens on marked spots on a timer; enough tokens in a player's bags get swapped
    // for the reward kit, so the mode gets a callback instead of a raw item pickup.
    public sealed class PickupSpawner
    {
        // Stack attribute that tells our tokens from the same item brought in.
        private const string TokenKey = "canevents_pickup";

        public const string DefaultTokenCode = "game:stick";

        // Key suffixes after the caller's prefix ("bow" + ItemKey = "bowPickupItem"). Admin panel reads these too.
        public const string ItemKey = "PickupItem";
        public const string QuantityKey = "PickupQuantity";
        public const string FirstSecondsKey = "PickupFirstSeconds";
        public const string IntervalSecondsKey = "PickupIntervalSeconds";
        public const string MaxActiveKey = "PickupMaxActive";
        public const string MinDistanceKey = "PickupMinDistance";
        public const string LifetimeSecondsKey = "PickupLifetimeSeconds";
        public const string RequiredKey = "PickupRequired";
        public const string RewardKey = "PickupReward";

        public const int DefaultQuantity = 1;
        public const float DefaultFirstSeconds = 60f;
        public const float DefaultIntervalSeconds = 90f;
        public const int DefaultMaxActive = 1;
        public const float DefaultMinDistance = 12f;
        // 0 = never expires
        public const float DefaultLifetimeSeconds = 0f;
        public const int DefaultRequired = 10;

        private sealed class RewardEntry
        {
            public string Code;
            public int Quantity = 1;
        }

        private string tokenCode;
        private int tokenQuantity = 1;
        private float firstSpawnSeconds = 60f;
        private float intervalSeconds = 90f;
        private int maxActive = 1;
        private float minDistance = 12f;
        private float lifetimeSeconds;      // 0 = never expires

        // Tokens per reward.
        private int requiredCount = 10;

        private readonly List<RewardEntry> reward = new List<RewardEntry>();

        // Last seen token count per player, to report progress only on change.
        private readonly Dictionary<string, int> lastCounted = new Dictionary<string, int>();

        private readonly List<BlockPos> spots = new List<BlockPos>();

        // entity id -> match time spawned, for expiry
        private readonly Dictionary<long, float> spawnedAt = new Dictionary<long, float>();

        private float nextSpawnAt = float.MaxValue;

        public Action<IServerPlayer, List<ItemStack>> OnClaimed;

        // (player, carried, required)
        public Action<IServerPlayer, int, int> OnProgress;

        public bool Enabled => !string.IsNullOrEmpty(tokenCode) && reward.Count > 0;

        public bool HasSpots => spots.Count > 0;

        public string DisabledReason
            => string.IsNullOrEmpty(tokenCode)
                ? "no item to drop is configured (bowPickupItem)"
                : reward.Count == 0
                    ? "an item is configured but nothing to pay out (bowPickupReward is empty)"
                    : "enabled";

        public string Describe()
            => string.Format("{0} x{1} on {2} spot(s), first at {3}s then every {4}s, "
                + "up to {5} at once, {6} blocks apart, {7} needed for the reward",
                tokenCode, tokenQuantity, spots.Count, firstSpawnSeconds, intervalSeconds,
                maxActive, minDistance, requiredCount);

        public void Parse(BehaviorAttributes cfg, string prefix)
        {
            // Blank item code turns spawns off.
            tokenCode = cfg.String(prefix + ItemKey, DefaultTokenCode);
            if (string.IsNullOrWhiteSpace(tokenCode)) tokenCode = null;
            tokenQuantity = Math.Max(1, cfg.Int(prefix + QuantityKey, DefaultQuantity));
            firstSpawnSeconds = Math.Max(0f, cfg.Float(prefix + FirstSecondsKey, DefaultFirstSeconds));
            intervalSeconds = Math.Max(0f, cfg.Float(prefix + IntervalSecondsKey, DefaultIntervalSeconds));
            maxActive = Math.Max(1, cfg.Int(prefix + MaxActiveKey, DefaultMaxActive));
            minDistance = Math.Max(0f, cfg.Float(prefix + MinDistanceKey, DefaultMinDistance));
            lifetimeSeconds = Math.Max(0f, cfg.Float(prefix + LifetimeSecondsKey, DefaultLifetimeSeconds));
            requiredCount = Math.Max(1, cfg.Int(prefix + RequiredKey, DefaultRequired));

            reward.Clear();
            var raw = cfg.Raw(prefix + RewardKey);
            if (raw?.Token is Newtonsoft.Json.Linq.JArray arr)
            {
                foreach (var entry in arr)
                {
                    string code = entry["code"]?.Value<string>();
                    if (string.IsNullOrWhiteSpace(code)) continue;
                    reward.Add(new RewardEntry
                    {
                        Code = code.Trim(),
                        Quantity = Math.Max(1, entry["quantity"]?.ToObject<int>() ?? 1)
                    });
                }
            }
        }

        public void Begin(RunningEvent runningEvent, string behaviorKey)
        {
            spots.Clear();
            spawnedAt.Clear();
            lastCounted.Clear();
            announcedFirstSpawn = false;
            nextSpawnAt = float.MaxValue;
            if (!Enabled) return;

            var place = runningEvent?.Place;
            if (place != null && place.TryGetBehaviorData(behaviorKey, out var data)
                && data["PickupSpots"] is Newtonsoft.Json.Linq.JArray arr)
            {
                foreach (var token in arr)
                    if (JsonPos.TryRead(token, out var pos)) spots.Add(pos);
            }

            if (spots.Count > 0) nextSpawnAt = firstSpawnSeconds;
        }

        public void Tick(RunningEvent runningEvent, Log log)
        {
            if (!Enabled || spots.Count == 0 || runningEvent == null) return;

            CollectClaims(runningEvent, log);
            ExpireOld(runningEvent);

            float elapsed = runningEvent.ElapsedSeconds;
            if (elapsed < nextSpawnAt) return;

            // Advance even if the spawn gets skipped, or a full arena retries every tick.
            nextSpawnAt = intervalSeconds > 0f ? elapsed + intervalSeconds : float.MaxValue;
            SpawnOne(runningEvent, log);
        }

        public void End(RunningEvent runningEvent)
        {
            spawnedAt.Clear();
            lastCounted.Clear();
            var sapi = runningEvent?.sapi;
            if (sapi == null || spots.Count == 0) return;

            foreach (var entity in FindTokens(runningEvent))
                sapi.World.DespawnEntity(entity, new EntityDespawnData { Reason = EnumDespawnReason.Removed });

            spots.Clear();
        }

        // Only on spots with no token within minDistance.
        private void SpawnOne(RunningEvent runningEvent, Log log)
        {
            var world = runningEvent.sapi.World;
            var live = FindTokens(runningEvent);
            if (live.Count >= maxActive)
            {
                log?.Debug("Pickup spawn skipped: {0} already lying around (max {1}).", live.Count, maxActive);
                return;
            }

            var candidates = new List<BlockPos>();
            float minDistSq = minDistance * minDistance;
            foreach (var spot in spots)
            {
                bool taken = false;
                foreach (var entity in live)
                {
                    double dx = entity.Pos.X - (spot.X + 0.5);
                    double dy = entity.Pos.Y - (spot.Y + 1.0);
                    double dz = entity.Pos.Z - (spot.Z + 0.5);
                    if (dx * dx + dy * dy + dz * dz <= minDistSq) { taken = true; break; }
                }
                if (!taken) candidates.Add(spot);
            }
            if (candidates.Count == 0)
            {
                log?.Debug("Pickup spawn skipped: every one of the {0} spot(s) has a token within "
                    + "{1} blocks already.", spots.Count, minDistance);
                return;
            }

            var pos = candidates[world.Rand.Next(candidates.Count)];
            var stack = Resolve(world, tokenCode, tokenQuantity);
            if (stack == null)
            {
                log?.Warning("Pickup item '{0}' does not resolve; nothing spawned.", tokenCode);
                return;
            }
            stack.Attributes.SetBool(TokenKey, true);
            // Drops on death so tokens can be looted off a body.
            IssuedGear.MarkDropOnDeath(stack);

            world.SpawnItemEntity(stack, new Vec3d(pos.X + 0.5, pos.Y + 1.0, pos.Z + 0.5));

            foreach (var entity in FindTokens(runningEvent))
                if (!spawnedAt.ContainsKey(entity.EntityId))
                    spawnedAt[entity.EntityId] = runningEvent.ElapsedSeconds;

            if (!announcedFirstSpawn)
            {
                announcedFirstSpawn = true;
                log?.Notification("First pickup token dropped at {0},{1},{2}.", pos.X, pos.Y, pos.Z);
            }
            else log?.Debug("Pickup token spawned at {0},{1},{2}.", pos.X, pos.Y, pos.Z);
        }

        private bool announcedFirstSpawn;

        private void ExpireOld(RunningEvent runningEvent)
        {
            if (lifetimeSeconds <= 0f) return;

            float now = runningEvent.ElapsedSeconds;
            foreach (var entity in FindTokens(runningEvent))
            {
                if (!spawnedAt.TryGetValue(entity.EntityId, out var born))
                {
                    spawnedAt[entity.EntityId] = now;   // a token from before a reconnect; age it from here
                    continue;
                }
                if (now - born < lifetimeSeconds) continue;

                runningEvent.sapi.World.DespawnEntity(entity,
                    new EntityDespawnData { Reason = EnumDespawnReason.Removed });
                spawnedAt.Remove(entity.EntityId);
            }
        }

        // Scans inventories instead of hooking pickup, so traded or looted tokens count too.
        private void CollectClaims(RunningEvent runningEvent, Log log)
        {
            var sapi = runningEvent.sapi;
            foreach (var player in runningEvent.OnlineParticipants)
            {
                if (player?.InventoryManager == null) continue;
                if (!runningEvent.IsActive(player.PlayerUID)) continue;

                int carried = CountTokens(player);
                int paid = 0;
                while (carried >= requiredCount)
                {
                    TakeTokens(player, requiredCount);
                    carried -= requiredCount;
                    PayOut(sapi, player, log);
                    paid++;
                }

                ReportProgress(player, carried, paid > 0);
            }
        }

        private void PayOut(ICoreServerAPI sapi, IServerPlayer player, Log log)
        {
            var given = new List<ItemStack>();
            foreach (var entry in reward)
            {
                var stack = Resolve(sapi.World, entry.Code, entry.Quantity);
                if (stack == null)
                {
                    log?.Warning("Pickup reward '{0}' does not resolve; skipped.", entry.Code);
                    continue;
                }
                // Marked as issued so the loadout drop/sweep rules apply.
                IssuedGear.Mark(stack, player.PlayerUID);
                InventoryUtil.Give(sapi, player, stack, "pickup-reward");
                given.Add(stack);
            }
            if (given.Count > 0) OnClaimed?.Invoke(player, given);
        }

        private void ReportProgress(IServerPlayer player, int carried, bool justPaid)
        {
            bool known = lastCounted.TryGetValue(player.PlayerUID, out int previous);
            lastCounted[player.PlayerUID] = carried;
            if (justPaid || !known || carried == previous || carried == 0) return;
            if (carried > previous) OnProgress?.Invoke(player, carried, requiredCount);
        }

        private static int CountTokens(IServerPlayer player)
        {
            int total = 0;
            foreach (var inv in player.InventoryManager.Inventories.Values)
            {
                if (inv.ClassName == "creative") continue;
                for (int i = 0; i < inv.Count; i++)
                {
                    var stack = inv[i]?.Itemstack;
                    if (stack?.Attributes?.GetBool(TokenKey) == true) total += stack.StackSize;
                }
            }
            return total;
        }

        private static void TakeTokens(IServerPlayer player, int count)
        {
            foreach (var inv in player.InventoryManager.Inventories.Values)
            {
                if (inv.ClassName == "creative") continue;
                for (int i = 0; i < inv.Count && count > 0; i++)
                {
                    var slot = inv[i];
                    var stack = slot?.Itemstack;
                    if (stack?.Attributes?.GetBool(TokenKey) != true) continue;

                    int take = Math.Min(count, stack.StackSize);
                    stack.StackSize -= take;
                    count -= take;
                    if (stack.StackSize <= 0) slot.Itemstack = null;
                    slot.MarkDirty();
                }
            }
        }

        private List<Entity> FindTokens(RunningEvent runningEvent)
        {
            var result = new List<Entity>();
            var sapi = runningEvent.sapi;

            // Place bounds, else a box around the spots padded by minDistance.
            BlockPos from, to;
            if (!PlaceBounds.TryCuboid(runningEvent.Place, 3, sapi.World.BlockAccessor.MapSizeY,
                    out from, out to))
            {
                if (spots.Count == 0) return result;
                int margin = (int)Math.Ceiling(minDistance) + 3;
                int minX = int.MaxValue, minZ = int.MaxValue, maxX = int.MinValue, maxZ = int.MinValue;
                int dim = spots[0].dimension;
                foreach (var spot in spots)
                {
                    minX = Math.Min(minX, spot.X); maxX = Math.Max(maxX, spot.X);
                    minZ = Math.Min(minZ, spot.Z); maxZ = Math.Max(maxZ, spot.Z);
                }
                from = new BlockPos(minX - margin, 0, minZ - margin, dim);
                to = new BlockPos(maxX + margin, sapi.World.BlockAccessor.MapSizeY, maxZ + margin, dim);
            }

            var found = sapi.World.GetEntitiesInsideCuboid(from, to,
                e => e is EntityItem item && item.Itemstack?.Attributes?.GetBool(TokenKey) == true);
            if (found != null) result.AddRange(found);
            return result;
        }

        private static ItemStack Resolve(IWorldAccessor world, string code, int quantity)
        {
            if (string.IsNullOrWhiteSpace(code)) return null;
            var loc = new AssetLocation(code);
            var item = world.GetItem(loc);
            if (item != null) return new ItemStack(item, quantity);
            var block = world.GetBlock(loc);
            return block != null ? new ItemStack(block, quantity) : null;
        }
    }
}
