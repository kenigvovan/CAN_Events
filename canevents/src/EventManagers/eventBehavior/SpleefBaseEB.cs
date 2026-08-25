using System;
using System.Collections.Generic;
using canevents.src.EventManagers;
using canevents.src.EventManagers.eventBehavior.Shared;
using canevents.src.EventManagers.events;
using canevents.src.EventPlacesManagers.Places;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace canevents.src.EventManagers.eventBehavior
{
    public abstract class SpleefBaseEB : CANEventBehavior, Shared.IStartCryProvider, Shared.IMatchScorer
    {
        public string StartCryLangKey => "canevents:spleef-start-cry";

        /// <summary>Undone first thing in OnEnd - see Unsubscribers for why that order matters.</summary>
        private readonly Shared.Unsubscribers subscriptions = new Shared.Unsubscribers();


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
        // Who is out is the roster's business (runningEvent.IsActive); this only keeps the ORDER,
        // which the roster does not. Drives placement: last out = highest place. Shared with
        // Hunger Games - see SurvivalRanking.
        protected readonly Shared.SurvivalRanking survival = new Shared.SurvivalRanking();

        protected BlockPos cageOrigin;
        protected int cageWidth;
        protected int cageDepth;
        protected int cageHeight;
        protected string cageBlockCode;
        protected LandClaim cageClaim;

        protected SpleefBaseEB(string name, JsonObject attributes, RunningEvent runningEvent)
            : base(name, attributes, runningEvent) { }

        public override void Initialize()
        {
            platformWidth = Config.Int("platformWidth", 20);
            platformDepth = Config.Int("platformDepth", 20);
            blockCode = Config.String("blockCode", "game:stickslayer-wooden-ew-free");
        }

        protected void LoadCageFromPlace()
        {
            var place = runningEvent.Place;
            if (place == null || !place.TryGetBehaviorData(BehaviorKeys.Spleef, out var data)) return;
            if (data["CageOriginX"] == null) return;

            cageOrigin    = new BlockPos(data.Value<int>("CageOriginX"), data.Value<int>("CageOriginY"), data.Value<int>("CageOriginZ"), 0);
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

            for (int x = 0; x < cageWidth; x++)
            for (int y = 0; y < cageHeight; y++)
            for (int z = 0; z < cageDepth; z++)
            {
                bool isShell = x == 0 || x == cageWidth - 1 || y == 0 || y == cageHeight - 1 || z == 0 || z == cageDepth - 1;
                var pos = new BlockPos(cageOrigin.X + x, cageOrigin.Y + y, cageOrigin.Z + z, cageOrigin.dimension);
                savedCageBlocks[pos.Copy()] = blockAccessor.GetBlock(pos).BlockId;
                blockAccessor.SetBlock(isShell ? block.BlockId : airBlockId, pos);
            }

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

            var blockAccessor = runningEvent.sapi.World.BlockAccessor;
            foreach (var kvp in savedCageBlocks)
                blockAccessor.SetBlock(kvp.Value, kvp.Key);

            blockAccessor.Commit();
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

        protected void GeneratePlatform()
        {
            savedBlocks.Clear();
            var blockAccessor = runningEvent.sapi.World.BlockAccessor;

            var customPositions = LoadPlatformPositions();
            if (customPositions != null)
            {
                platformY = int.MaxValue;
                foreach (var pos in customPositions)
                {
                    if (pos.Y < platformY) platformY = pos.Y;
                    savedBlocks[pos.Copy()] = blockAccessor.GetBlock(pos).BlockId;
                    blockAccessor.SetBlock(spleefBlockId, pos);
                }
                blockAccessor.Commit();
                AfterPlatformPlaced(customPositions);
                log.Notification("Placed {0} custom platform blocks, lowest Y={1}", customPositions.Count, platformY);
                return;
            }

            var rectPositions = new List<BlockPos>();
            for (int x = 0; x < platformWidth; x++)
            for (int z = 0; z < platformDepth; z++)
            {
                var pos = new BlockPos(platformOrigin.X + x, platformY, platformOrigin.Z + z, platformOrigin.dimension);
                savedBlocks[pos.Copy()] = blockAccessor.GetBlock(pos).BlockId;
                blockAccessor.SetBlock(spleefBlockId, pos);
                rectPositions.Add(pos.Copy());
            }

            blockAccessor.Commit();
            AfterPlatformPlaced(rectPositions);
            log.Notification("Generated {0}x{1} platform at Y={2}", platformWidth, platformDepth, platformY);
        }

        protected virtual void AfterPlatformPlaced(List<BlockPos> positions) { }

        // The platform has no LandClaim (only the cage does), so anyone in reach could mine it for
        // free blocks. The guard is therefore unconditional on every spleef mode: it used to sit in
        // SpleefEB behind "meltDelay > 0", which left melt-less configs handing out free stacks.

        public override void OnStart()
        {
            runningEvent.sapi.Event.BreakBlock += OnBreakPlatformBlock;
            subscriptions.Add(() => runningEvent.sapi.Event.BreakBlock -= OnBreakPlatformBlock);
        }

        private void OnBreakPlatformBlock(IServerPlayer player, BlockSelection sel,
            ref float dropMult, ref EnumHandling handling)
        {
            if (runningEvent.sapi.World.BlockAccessor.GetBlock(sel.Position).BlockId != spleefBlockId) return;

            // No drops and no removal, for everyone. What a hit MEANS is the mode's business.
            handling = EnumHandling.PreventDefault;
            dropMult = 0f;

            if (!runningEvent.IsActive(player.PlayerUID)) return;
            OnPlatformBlockHit(player, sel.Position);
        }

        /// <summary>What a dig on a platform block does in this mode, for a player still in play.
        /// The block itself is already protected by the time this runs.</summary>
        protected virtual void OnPlatformBlockHit(IServerPlayer player, BlockPos pos) { }

        protected void RestorePlatform()
        {
            var blockAccessor = runningEvent.sapi.World.BlockAccessor;
            foreach (var kvp in savedBlocks)
                blockAccessor.SetBlock(kvp.Value, kvp.Key);

            blockAccessor.Commit();
            savedBlocks.Clear();
            log.Notification("Platform restored");
        }

        protected void BroadcastMessage(string message)
            => EventBroadcaster.Broadcast(runningEvent, message);

        protected virtual bool UsesCage => true;

        // Pre-fight: players stay caged until the countdown runs out. Shared by both spleef modes,
        // which differ only in what happens the moment the cage falls (OnCageOpened).

        private float prefightSeconds = 3f;
        private readonly PrefightTimer prefight = new PrefightTimer();

        /// <summary>Read the countdown length. Call from Initialize().</summary>
        protected void ParsePrefightSeconds(float defaultSeconds = 3f)
            => prefightSeconds = Config.Float("prefightSeconds", defaultSeconds);

        /// <summary>Begin the caged countdown. Call from OnStart(), after TPThereAndBack has put
        /// everyone inside the cage.</summary>
        protected void StartPrefight()
        {
            prefight.Start(prefightSeconds);
            EventBroadcaster.HealAll(runningEvent);

            if (!prefight.Active)
            {
                OpenCage();   // countdown disabled: straight into the fight
                return;
            }
            BroadcastMessage(Lang.Get("canevents:countdown-get-ready", (int)Math.Ceiling(prefightSeconds)));
        }

        /// <summary>Advance the countdown. Returns true while the cage is still shut, meaning the
        /// caller should skip its own tick work (fall checks must not fire on caged players).</summary>
        protected bool TickPrefight(float dt)
            => prefight.Tick(dt,
                second => BroadcastMessage(Lang.Get("canevents:countdown-tick", second)),
                OpenCage);

        /// <summary>Whether the fight is on (used by tick work that must ignore the caged phase).</summary>
        protected bool PrefightActive => prefight.Active;

        private void OpenCage()
        {
            DestroyCage();
            BroadcastMessage(Lang.Get("canevents:spleef-start-cry"));
            OnCageOpened();
        }

        /// <summary>What this mode does the instant the cage falls: hand out shovels, start a
        /// melt-detection listener, ...</summary>
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

            GeneratePlatform();
            // platformY is updated inside GeneratePlatform to reflect actual lowest placed block.
            fallThreshold = platformY - 2f;
            log.Notification("fallThreshold set to Y={0}", fallThreshold);
        }

        // Reused across ticks; only grows when someone falls on this tick (rare).
        private readonly List<string> _fallenThisTick = new List<string>();

        public override void OnTick(float dt)
        {
            // Iterate directly; fallers are collected so EliminatePlayer's mutation of the
            // collection happens after the loop.
            foreach (var plGuid in runningEvent.ParticipantsStillInEvent)
            {
                var player = runningEvent.sapi.World.PlayerByUid(plGuid);
                if (player == null) continue;

                if (player.Entity.Pos.Y < fallThreshold)
                {
                    survival.Add(plGuid);
                    _fallenThisTick.Add(plGuid);
                }
            }

            if (_fallenThisTick.Count == 0) return;

            for (int i = 0; i < _fallenThisTick.Count; i++)
            {
                var plGuid = _fallenThisTick[i];
                var player = runningEvent.sapi.World.PlayerByUid(plGuid);
                runningEvent.EliminatePlayer(plGuid);

                string name = player?.PlayerName ?? plGuid;
                BroadcastMessage(Lang.Get("canevents:spleef-player-fell", name, runningEvent.ParticipantsStillInEvent.Count));

                if (runningEvent.NetworkHandler != null)
                    runningEvent.NetworkHandler.SendKillFeed(runningEvent, "the void", name, "swallowed");

                log.Notification("{0} fell", name);
            }
            _fallenThisTick.Clear();
        }

        // The block doesn't declare entityClass=SpleefMelt, so SetBlock does NOT auto-clean these BEs;
        // left behind, their melt tick later punches holes in the restored terrain. Must run before
        // RestorePlatform, while savedBlocks still lists the positions.
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
            subscriptions.UnhookAll();

            // Drop live melt BEs first, or their timers outlive the match and delete restored terrain.
            RemoveMeltEntities();

            // Platform BEFORE the cage: GeneratePlatform saved its "originals" after BuildCage had
            // aired out the cage interior, so on overlapping positions savedBlocks holds air and only
            // savedCageBlocks has the real terrain - DestroyCage must therefore run last and win.
            RestorePlatform();
            DestroyCage();

            var winners = runningEvent.GetWinners();
            if (winners.Count > 0)
            {
                var names = new List<string>();
                foreach (var uid in winners)
                {
                    var pl = runningEvent.sapi.World.PlayerByUid(uid);
                    if (pl != null) names.Add(pl.PlayerName);
                }
                BroadcastMessage(Lang.Get("canevents:spleef-winner", string.Join(", ", names)));
            }
        }

        public override void OnParticipantDeath(IServerPlayer player, DamageSource source)
        {
            if (runningEvent.IsActive(player.PlayerUID))
            {
                survival.Add(player.PlayerUID);
                runningEvent.EliminatePlayer(player.PlayerUID);

                BroadcastMessage(Lang.Get("canevents:spleef-player-died", player.PlayerName, runningEvent.ParticipantsStillInEvent.Count));
            }
        }

        // IMatchScorer (spleef has no kills/deaths)
        public Dictionary<string, int> GetFinalKillCounts() => null;
        public Dictionary<string, int> GetFinalDeathCounts() => null;

        /// <summary>
        /// Placement: survivor(s) first, then eliminated players in reverse fall order. Spleef has no
        /// kills, so nothing breaks a survival tie. Parties (unreachable on shipped solo-only configs)
        /// are ranked like Hunger Games duos: a party lives until its last member falls.
        /// </summary>
        public List<HashSet<string>> GetFinalRanking() => survival.Rank(runningEvent);

        public override void OnParticipantRespawn(IServerPlayer player) { }
    }
}
