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
    /// <summary>
    /// Drops a collectible token on the spots an admin marked on the place, on a timer, and pays
    /// whoever picks it up with a kit of items.
    ///
    /// The token is the whole point of the indirection: a bow lying on the floor would be picked up
    /// by whoever walked past first and no mode could react to it. A token is inert - it is looked
    /// for in the finder's bags on the next tick, taken back out, and the real reward is handed over
    /// there, so the mode gets a "this player just armed themselves" moment it can announce, conceal
    /// or refuse.
    ///
    /// Nothing here is Murder Mystery specific; that mode is only the first customer.
    /// </summary>
    public sealed class PickupSpawner
    {
        /// <summary>Marks a stack as one of ours, so a player who brought the same item from home is
        /// not paid for it and the arena sweep can tell them apart.</summary>
        private const string TokenKey = "canevents_pickup";

        /// <summary>What gets dropped when the event config names nothing.</summary>
        public const string DefaultTokenCode = "game:stick";

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
        private float lifetimeSeconds;      // 0 = it lies there until someone takes it

        /// <summary>How many tokens buy one reward. The collection IS the mechanic: one pickup is a
        /// lottery win, ten of them is a player who spent the match looking for them.</summary>
        private int requiredCount = 10;

        private readonly List<RewardEntry> reward = new List<RewardEntry>();

        /// <summary>Tokens each player was last seen carrying, so progress is only announced when it
        /// actually changed (this runs once a second).</summary>
        private readonly Dictionary<string, int> lastCounted = new Dictionary<string, int>();

        /// <summary>Marked spots read off the place at match start.</summary>
        private readonly List<BlockPos> spots = new List<BlockPos>();

        /// <summary>Entity id -> match time it was spawned at, for the lifetime sweep.</summary>
        private readonly Dictionary<long, float> spawnedAt = new Dictionary<long, float>();

        private float nextSpawnAt = float.MaxValue;

        /// <summary>Called with the player and what they were given, once per claimed token. The mode
        /// decides what that means - a message, a role change, hiding the weapon again.</summary>
        public Action<IServerPlayer, List<ItemStack>> OnClaimed;

        /// <summary>Called with (player, carried, required) when someone's tally goes up without
        /// reaching the price yet. The mode decides whether and how to tell them.</summary>
        public Action<IServerPlayer, int, int> OnProgress;

        /// <summary>Nothing to spawn or nothing to pay with means the whole feature is off.</summary>
        public bool Enabled => !string.IsNullOrEmpty(tokenCode) && reward.Count > 0;

        /// <summary>Did the place mark any spots? Off-by-configuration and off-by-arena read
        /// differently to an admin, so the mode can log them apart.</summary>
        public bool HasSpots => spots.Count > 0;

        /// <summary>Why <see cref="Enabled"/> is false, in words an admin can act on.</summary>
        public string DisabledReason
            => string.IsNullOrEmpty(tokenCode)
                ? "no item to drop is configured (bowPickupItem)"
                : reward.Count == 0
                    ? "an item is configured but nothing to pay out (bowPickupReward is empty)"
                    : "enabled";

        /// <summary>What this spawner is about to do, for the match log.</summary>
        public string Describe()
            => string.Format("{0} x{1} on {2} spot(s), first at {3}s then every {4}s, "
                + "up to {5} at once, {6} blocks apart, {7} needed for the reward",
                tokenCode, tokenQuantity, spots.Count, firstSpawnSeconds, intervalSeconds,
                maxActive, minDistance, requiredCount);

        /// <summary>
        /// Reads the token, the timers and the reward off the behavior's attributes.
        /// <paramref name="prefix"/> namespaces the keys, so one mode can run two of these.
        /// </summary>
        public void Parse(BehaviorAttributes cfg, string prefix)
        {
            // A stick by default: common enough that carrying one is not itself an accusation, and
            // nobody can bring their own into a match that clears the inventory at the start. Blank
            // it in the config to turn the spawns off.
            tokenCode = cfg.String(prefix + "PickupItem", DefaultTokenCode);
            if (string.IsNullOrWhiteSpace(tokenCode)) tokenCode = null;
            tokenQuantity = Math.Max(1, cfg.Int(prefix + "PickupQuantity", 1));
            firstSpawnSeconds = Math.Max(0f, cfg.Float(prefix + "PickupFirstSeconds", 60f));
            intervalSeconds = Math.Max(0f, cfg.Float(prefix + "PickupIntervalSeconds", 90f));
            maxActive = Math.Max(1, cfg.Int(prefix + "PickupMaxActive", 1));
            minDistance = Math.Max(0f, cfg.Float(prefix + "PickupMinDistance", 12f));
            lifetimeSeconds = Math.Max(0f, cfg.Float(prefix + "PickupLifetimeSeconds", 0f));
            requiredCount = Math.Max(1, cfg.Int(prefix + "PickupRequired", 10));

            reward.Clear();
            var raw = cfg.Raw(prefix + "PickupReward");
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

        /// <summary>Reads this place's marked spots and arms the first timer. Call at match start.</summary>
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

        /// <summary>
        /// One pass: pay anyone carrying a token, expire the ones nobody took, and drop a new one
        /// when the timer says so. Safe to call every second.
        /// </summary>
        public void Tick(RunningEvent runningEvent, Log log)
        {
            if (!Enabled || spots.Count == 0 || runningEvent == null) return;

            CollectClaims(runningEvent, log);
            ExpireOld(runningEvent);

            float elapsed = runningEvent.ElapsedSeconds;
            if (elapsed < nextSpawnAt) return;

            // The clock moves on either way: a skipped spawn (everything already occupied) must not
            // fire again on the very next tick, or a full arena would retry once a second forever.
            nextSpawnAt = intervalSeconds > 0f ? elapsed + intervalSeconds : float.MaxValue;
            SpawnOne(runningEvent, log);
        }

        /// <summary>Takes every token this spawner put on the arena back out of the world.</summary>
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

        /// <summary>
        /// Drops one token on a free spot. "Free" is the whole rule the admins asked for: never a
        /// second token within <c>minDistance</c> of one already lying there, so a spot cannot pile
        /// up into a stash while the rest of the arena stays empty.
        /// </summary>
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
            // Falls out of the body rather than vanishing with the rest of the match inventory: half a
            // collection lying next to its owner is the reward for the kill, and the reason carrying
            // one is a risk. Not marked as issued gear, so the drop policy leaves it alone otherwise.
            IssuedGear.MarkDropOnDeath(stack);

            // One block above the marked floor block, and motionless: a token that rolls off its
            // spot would drift out of the minimum-distance test that keeps them apart.
            world.SpawnItemEntity(stack, new Vec3d(pos.X + 0.5, pos.Y + 1.0, pos.Z + 0.5));

            foreach (var entity in FindTokens(runningEvent))
                if (!spawnedAt.ContainsKey(entity.EntityId))
                    spawnedAt[entity.EntityId] = runningEvent.ElapsedSeconds;

            // The first one of the match goes in the log properly: "did anything spawn at all" is the
            // question an admin actually asks, and the rest are noise once it is answered.
            if (!announcedFirstSpawn)
            {
                announcedFirstSpawn = true;
                log?.Notification("First pickup token dropped at {0},{1},{2}.", pos.X, pos.Y, pos.Z);
            }
            else log?.Debug("Pickup token spawned at {0},{1},{2}.", pos.X, pos.Y, pos.Z);
        }

        private bool announcedFirstSpawn;

        /// <summary>Removes tokens nobody claimed in time, so the arena does not fill up with them
        /// while the survivors are busy elsewhere. 0 turns the expiry off.</summary>
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

        /// <summary>
        /// Pays out once a participant is carrying <see cref="requiredCount"/> tokens, and reports
        /// their tally while they are still short of it.
        ///
        /// Scanning their bags beats hooking the pickup event: a token can also arrive by being handed
        /// over, thrown or looted off a body, and all four should count the same. It also means the
        /// tokens are a real, visible, droppable resource in the meantime - a player can be robbed of
        /// a nearly-finished collection, which is the point of collecting one.
        /// </summary>
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
                    // Take the price out of their bags, then hand the reward over. Leftovers stay:
                    // 12 tokens with a price of 10 buys one bow and keeps the other two.
                    TakeTokens(player, requiredCount);
                    carried -= requiredCount;
                    PayOut(sapi, player, log);
                    paid++;
                }

                ReportProgress(player, carried, paid > 0);
            }
        }

        /// <summary>Hands over one reward kit.</summary>
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
                // Issued kit, so the match's own drop/sweep policy applies to it exactly as it does
                // to the loadout everyone started with.
                IssuedGear.Mark(stack, player.PlayerUID);
                InventoryUtil.Give(sapi, player, stack, "pickup-reward");
                given.Add(stack);
            }
            if (given.Count > 0) OnClaimed?.Invoke(player, given);
        }

        /// <summary>
        /// Tells the player how far along they are, but only when the number actually moved. This runs
        /// every second: reporting unconditionally would put a line in their chat once a second for
        /// the whole match.
        /// </summary>
        private void ReportProgress(IServerPlayer player, int carried, bool justPaid)
        {
            bool known = lastCounted.TryGetValue(player.PlayerUID, out int previous);
            lastCounted[player.PlayerUID] = carried;
            if (justPaid || !known || carried == previous || carried == 0) return;
            // Only picking up is news; spending or losing tokens explains itself.
            if (carried > previous) OnProgress?.Invoke(player, carried, requiredCount);
        }

        /// <summary>How many tokens this player is carrying, stack sizes included.</summary>
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

        /// <summary>Takes exactly <paramref name="count"/> tokens back out, partial stacks included.</summary>
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

        /// <summary>Every token of ours currently lying on the arena.</summary>
        private List<Entity> FindTokens(RunningEvent runningEvent)
        {
            var result = new List<Entity>();
            var sapi = runningEvent.sapi;

            // Bounds when the place has them, else a box around the marked spots: the spots ARE the
            // only places a token of ours can start from, and minDistance bounds how far it rolls.
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
