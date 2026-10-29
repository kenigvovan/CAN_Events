using System;
using System.Collections.Generic;
using canevents.src.EventManagers;
using canevents.src.EventManagers.eventBehavior.Shared;
using canevents.src.EventManagers.events;
using canevents.src.EventPlacesManagers.Places;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace canevents.src.EventManagers.eventBehavior
{
    public abstract class SpleefBaseEB : CANEventBehavior, Shared.IStartCryProvider, Shared.IMatchScorer
    {
        public string StartCryLangKey => "canevents:spleef-start-cry";

        // Released first in OnEnd, see Unsubscribers.
        private readonly ArenaBlockGuard blockGuard = new ArenaBlockGuard();


        protected int platformWidth = 20;
        protected int platformDepth = 20;
        protected int platformY = 0;
        protected float fallThreshold = 0f;
        protected BlockPos platformOrigin;
        protected int spleefBlockId;
        protected int airBlockId;
        protected string blockCode = "game:stickslayer-wooden-ew-free";

        protected Dictionary<BlockPos, int> savedBlocks = new Dictionary<BlockPos, int>();
        protected Dictionary<BlockPos, int> savedCageBlocks = new Dictionary<BlockPos, int>();
        // Elimination order for placement; the roster doesn't keep order.
        protected readonly Shared.SurvivalRanking survival = new Shared.SurvivalRanking();

        protected BlockPos cageOrigin;
        protected int cageWidth;
        protected int cageDepth;
        protected int cageHeight;
        protected string cageBlockCode;
        protected LandClaim cageClaim;

        protected SpleefBaseEB(string name, JsonObject attributes, RunningEvent runningEvent)
            : base(name, attributes, runningEvent) { }

        // Sealed so WarnUnknown runs after the subclass reads its keys.
        public sealed override void Initialize()
        {
            platformWidth = Config.Int("platformWidth", 20);
            platformDepth = Config.Int("platformDepth", 20);
            blockCode = Config.String("blockCode", "game:stickslayer-wooden-ew-free");
            prefightSeconds = PrefightTimer.ReadSeconds(Config);
            ReadModeConfig();
            Config.WarnUnknown();
        }

        protected virtual void ReadModeConfig() { }

        protected void LoadCageFromPlace()
        {
            var place = runningEvent.Place;
            if (place == null || !place.TryGetBehaviorData(BehaviorKeys.Spleef, out var data)) return;
            if (data["CageOriginX"] == null) return;

            // Older saves have no CageOriginDim (dim 0).
            cageOrigin    = new BlockPos(data.Value<int>("CageOriginX"), data.Value<int>("CageOriginY"), data.Value<int>("CageOriginZ"),
                                         data["CageOriginDim"] != null ? data.Value<int>("CageOriginDim") : 0);
            cageWidth     = data["CageWidth"]  != null ? data.Value<int>("CageWidth")    : 5;
            cageDepth     = data["CageDepth"]  != null ? data.Value<int>("CageDepth")    : 5;
            cageHeight    = data["CageHeight"] != null ? data.Value<int>("CageHeight")   : 4;
            cageBlockCode = data["CageBlock"]  != null ? data.Value<string>("CageBlock") : "game:glass-plain";
        }

        protected void BuildCage()
        {
            if (cageOrigin == null)
            {
                log.Warning("BuildCage: cageOrigin is null, skipping cage build.");
                return;
            }

            log.Notification("BuildCage: origin=({0},{1},{2}) size={3}x{4}x{5} block={6}",
                cageOrigin.X, cageOrigin.Y, cageOrigin.Z, cageWidth, cageDepth, cageHeight, cageBlockCode);

            var block = runningEvent.sapi.World.GetBlock(new AssetLocation(cageBlockCode));
            if (block == null || block.BlockId == 0)
            {
                log.Error("Cage block '{0}' not found, cage will not be built.", cageBlockCode);
                return;
            }

            var blockAccessor = runningEvent.sapi.World.BlockAccessor;
            savedCageBlocks.Clear();

            var shell = new Dictionary<BlockPos, bool>();
            for (int x = 0; x < cageWidth; x++)
            for (int y = 0; y < cageHeight; y++)
            for (int z = 0; z < cageDepth; z++)
            {
                var pos = new BlockPos(cageOrigin.X + x, cageOrigin.Y + y, cageOrigin.Z + z, cageOrigin.dimension);
                savedCageBlocks[pos] = blockAccessor.GetBlock(pos).BlockId;
                shell[pos] = x == 0 || x == cageWidth - 1 || y == 0 || y == cageHeight - 1 || z == 0 || z == cageDepth - 1;
            }

            if (!ArenaBlockJournal.Record(runningEvent.Place?.Id, ArenaBlockJournal.TagSpleefCage, savedCageBlocks))
            {
                log.Error("Cage not built: its original blocks could not be recorded.");
                savedCageBlocks.Clear();
                return;
            }

            foreach (var kvp in shell)
                blockAccessor.SetBlock(kvp.Value ? block.BlockId : airBlockId, kvp.Key);

            blockAccessor.Commit();

            cageClaim = new LandClaim
            {
                Areas = new List<Cuboidi>
                {
                    new Cuboidi(
                        cageOrigin.X, cageOrigin.Y, cageOrigin.Z,
                        cageOrigin.X + cageWidth - 1, cageOrigin.Y + cageHeight - 1, cageOrigin.Z + cageDepth - 1
                    )
                },
                ProtectionLevel = 10,
                Description = "Spleef cage",
                LastKnownOwnerName = "System",
                AllowTraverseEveryone = true
            };
            runningEvent.sapi.World.Claims.Add(cageClaim);
        }

        protected void DestroyCage()
        {
            if (cageClaim != null)
            {
                runningEvent.sapi.World.Claims.Remove(cageClaim);
                cageClaim = null;
            }

            if (savedCageBlocks.Count == 0) return;

            ArenaBlockJournal.RestoreOr(runningEvent.Place?.Id, ArenaBlockJournal.TagSpleefCage,
                runningEvent.sapi.World.BlockAccessor, savedCageBlocks);
            savedCageBlocks.Clear();
            cageOrigin = null;
        }

        protected List<BlockPos> LoadPlatformPositions()
        {
            var place = runningEvent.Place;
            if (place == null || !place.TryGetBehaviorData(BehaviorKeys.Spleef, out var data)) return null;
            if (data["PlatformX"] == null || data["PlatformY"] == null || data["PlatformZ"] == null) return null;

            var xs = data["PlatformX"].ToObject<int[]>();
            var ys = data["PlatformY"].ToObject<int[]>();
            var zs = data["PlatformZ"].ToObject<int[]>();
            var ds = data["PlatformDim"]?.ToObject<int[]>();
            if (xs == null || ys == null || zs == null || xs.Length == 0
                || ys.Length != xs.Length || zs.Length != xs.Length)
            {
                log.Warning("Platform positions malformed (xs={0} ys={1} zs={2}) - ignoring.",
                    xs?.Length ?? -1, ys?.Length ?? -1, zs?.Length ?? -1);
                return null;
            }

            var list = new List<BlockPos>(xs.Length);
            for (int i = 0; i < xs.Length; i++)
                list.Add(new BlockPos(xs[i], ys[i], zs[i], ds != null ? ds[i] : 0));
            return list;
        }

        // false if the originals couldn't be journaled; nothing is placed then.
        protected bool GeneratePlatform()
        {
            savedBlocks.Clear();
            var blockAccessor = runningEvent.sapi.World.BlockAccessor;

            var positions = LoadPlatformPositions();
            bool custom = positions != null;
            if (custom)
            {
                platformY = int.MaxValue;
                foreach (var pos in positions)
                    if (pos.Y < platformY) platformY = pos.Y;
            }
            else
            {
                positions = new List<BlockPos>();
                for (int x = 0; x < platformWidth; x++)
                for (int z = 0; z < platformDepth; z++)
                    positions.Add(new BlockPos(platformOrigin.X + x, platformY, platformOrigin.Z + z, platformOrigin.dimension));
            }

            foreach (var pos in positions)
                savedBlocks[pos.Copy()] = blockAccessor.GetBlock(pos).BlockId;

            if (!ArenaBlockJournal.Record(runningEvent.Place?.Id, ArenaBlockJournal.TagSpleefPlatform, savedBlocks))
            {
                savedBlocks.Clear();
                return false;
            }

            foreach (var pos in positions)
                blockAccessor.SetBlock(spleefBlockId, pos);
            blockAccessor.Commit();
            AfterPlatformPlaced(positions);

            if (custom) log.Notification("Placed {0} custom platform blocks, lowest Y={1}", positions.Count, platformY);
            else log.Notification("Generated {0}x{1} platform at Y={2}", platformWidth, platformDepth, platformY);
            return true;
        }

        protected virtual void AfterPlatformPlaced(List<BlockPos> positions) { }

        // Platform has no LandClaim, so the break guard is always on or it can be mined for free blocks.
        public override void OnStart()
        {
            blockGuard.GuardBreaks(runningEvent, JudgePlatformBreak);
            StartPrefight();
        }

        private BlockBreakVerdict JudgePlatformBreak(IServerPlayer player, BlockPos pos)
        {
            if (!OwnsPlatformAt(pos)) return BlockBreakVerdict.NotGuarded;
            var ba = runningEvent.sapi.World.BlockAccessor;
            if (ba.GetBlock(pos).BlockId != spleefBlockId) return BlockBreakVerdict.NotGuarded;

            // Never drops. Only active players may break, and only if the mode allows.
            if (runningEvent.IsActive(player?.PlayerUID) && !OnPlatformBlockHit(player, pos))
            {
                // Melt BE isn't declared by the block, so the break won't remove it.
                if (ba.GetBlockEntity(pos) is BlockEntities.BlockEntitySpleefMelt)
                    ba.RemoveBlockEntity(pos);
                return BlockBreakVerdict.BreakWithoutDrop;
            }
            return BlockBreakVerdict.Keep;
        }

        // Melt BEs ask this to detect leftovers from a crashed match.
        public bool OwnsPlatformAt(BlockPos pos) => pos != null && savedBlocks.ContainsKey(pos);

        // false lets the dig break the block; true keeps it.
        protected virtual bool OnPlatformBlockHit(IServerPlayer player, BlockPos pos) => true;

        // startDelay null leaves the timers idle.
        protected void SpawnMeltEntities(IEnumerable<BlockPos> positions, float? startDelay)
        {
            var ba = runningEvent.sapi.World.BlockAccessor;
            int spawned = 0;
            foreach (var pos in positions)
            {
                ba.SpawnBlockEntity("SpleefMelt", pos);
                spawned++;
                if (startDelay is float delay)
                    (ba.GetBlockEntity(pos) as BlockEntities.BlockEntitySpleefMelt)?.StartMelt(runningEvent.sapi, delay);
            }
            log.Notification("Spawned {0} SpleefMelt BEs{1}", spawned, startDelay.HasValue ? " (melting)" : "");
        }

        protected void RestorePlatform()
        {
            if (savedBlocks.Count == 0) return;
            ArenaBlockJournal.RestoreOr(runningEvent.Place?.Id, ArenaBlockJournal.TagSpleefPlatform,
                runningEvent.sapi.World.BlockAccessor, savedBlocks);
            savedBlocks.Clear();
            log.Notification("Platform restored");
        }

        protected virtual bool UsesCage => true;

        private float prefightSeconds = 3f;
        private readonly PrefightTimer prefight = new PrefightTimer();

        // Runs after TPThereAndBack has put everyone in the cage.
        private void StartPrefight()
        {
            prefight.Start(prefightSeconds, runningEvent);
            EventBroadcaster.HealAll(runningEvent);

            if (!prefight.Active) OpenCage();   // countdown disabled
        }

        protected bool PrefightActive => prefight.Active;

        private void OpenCage()
        {
            DestroyCage();
            Announce(StartCryLangKey);
            OnCageOpened();
        }

        protected virtual void OnCageOpened() { }

        public override void OnBeforeStart()
        {
            survival.Clear();
            log.Notification("OnBeforeStart: place={0}", runningEvent.Place?.Name ?? "null");
            if (UsesCage)
            {
                LoadCageFromPlace();
                log.Notification("LoadCageFromPlace done: cageOrigin={0}", cageOrigin?.ToString() ?? "null");
                BuildCage();
            }

            var spleefBlock = runningEvent.sapi.World.GetBlock(new AssetLocation(blockCode));
            if (spleefBlock != null && spleefBlock.BlockId != 0)
            {
                spleefBlockId = spleefBlock.BlockId;
                log.Notification("Spleef block '{0}' id={1}", blockCode, spleefBlockId);
            }
            else
            {
                log.Error("Could not find block '{0}'!", blockCode);
                DestroyCage();
                // No platform, nobody can fall: end now instead of waiting for the time limit.
                runningEvent.RequestFinish("spleef block '" + blockCode + "' not found");
                return;
            }
            airBlockId = 0;

            var place = runningEvent.Place;
            if (place != null && place.HasBounds)
            {
                platformOrigin = place.BoundsMin.Copy();
                platformY = place.BoundsMin.Y;
                platformWidth = place.BoundsMax.X - place.BoundsMin.X + 1;
                platformDepth = place.BoundsMax.Z - place.BoundsMin.Z + 1;
            }
            else
            {
                var spawnPoints = place?.spawnPositions;
                if (spawnPoints == null || spawnPoints.Count == 0)
                {
                    log.Error("No spawn positions or bounds defined!");
                    return;
                }

                var center = spawnPoints[0];
                platformY = center.Y - 1;
                platformOrigin = new BlockPos(
                    center.X - platformWidth / 2,
                    platformY,
                    center.Z - platformDepth / 2,
                    center.dimension
                );
            }

            if (!GeneratePlatform())
            {
                log.Error("Platform not placed: its original blocks could not be recorded.");
                DestroyCage();
                runningEvent.RequestFinish("spleef platform could not be journaled");
                return;
            }
            // GeneratePlatform sets platformY to the lowest placed block.
            fallThreshold = platformY - 2f;
            log.Notification("fallThreshold set to Y={0}", fallThreshold);
            WarnIfNoPit();
        }

        // Solid ground right under the platform catches fallers above fallThreshold, so nobody is ever out.
        private void WarnIfNoPit()
        {
            var ba = runningEvent.sapi.World.BlockAccessor;
            int caught = 0, total = savedBlocks.Count;
            var probe = new BlockPos(0, 0, 0, platformOrigin?.dimension ?? 0);
            foreach (var pos in savedBlocks.Keys)
            {
                for (int dy = 1; dy <= 2; dy++)
                {
                    probe.Set(pos.X, pos.Y - dy, pos.Z);
                    if (ba.GetBlock(probe).SideSolid.Any) { caught++; break; }
                }
            }
            if (total > 0 && caught * 4 >= total)
            {
                string msg = string.Format(
                    "Spleef arena '{0}': {1} of {2} platform blocks have solid ground within 2 blocks below. "
                    + "Players who break through land on it and are never eliminated - dig a pit under the platform.",
                    runningEvent.Place?.Name, caught, total);
                log.Warning("{0}", msg);
            }
        }

        public override bool WantsTick => true;

        public override void OnTick(float dt)
        {
            if (prefight.Tick(dt, OpenCage)) return;

            // Bots included. LiveBodies is a snapshot, so eliminating inside the loop is safe.
            foreach (var (uid, body) in runningEvent.LiveBodies())
            {
                if (!VoidFall.HasFallen(body, fallThreshold)) continue;

                Eliminate(uid);
                string name = runningEvent.NameOf(uid);
                Announce("canevents:spleef-player-fell", name, runningEvent.ParticipantsStillInEvent.Count);
                VoidFall.AnnounceInFeed(runningEvent, name, "swallowed");
                log.Notification("{0} fell", name);
            }
        }

        private void Eliminate(string uid)
        {
            survival.Add(uid);
            runningEvent.EliminatePlayer(uid);
        }

        // Block doesn't declare entityClass=SpleefMelt, so SetBlock won't remove these BEs.
        // Call before RestorePlatform, which clears savedBlocks.
        protected void RemoveMeltEntities()
        {
            var ba = runningEvent.sapi.World.BlockAccessor;
            foreach (var pos in savedBlocks.Keys)
            {
                if (ba.GetBlockEntity(pos) is BlockEntities.BlockEntitySpleefMelt)
                    ba.RemoveBlockEntity(pos);
            }
        }

        public override void OnEnd()
        {
            blockGuard.Release();

            RemoveMeltEntities();

            // Cage last: on overlap savedBlocks holds the cage's air, savedCageBlocks the real terrain.
            RestorePlatform();
            DestroyCage();

            AnnounceWinners("canevents:spleef-winner", null, runningEvent.NameOf);
        }

        public override bool AnnouncesDeaths => true;

        public override void OnParticipantDeath(string uid, Entity body, DamageSource source)
        {
            if (runningEvent.IsActive(uid))
            {
                Eliminate(uid);
                Announce("canevents:spleef-player-died", runningEvent.NameOf(uid), runningEvent.ParticipantsStillInEvent.Count);
            }
        }

        // Survivors first, then reverse fall order. No kills, so no tiebreak.
        public List<HashSet<string>> GetFinalRanking() => survival.Rank(runningEvent);

        public override void OnParticipantRespawn(IServerPlayer player) { }
    }
}
