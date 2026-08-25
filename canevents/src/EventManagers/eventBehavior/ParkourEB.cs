using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers;
using canevents.src.EventManagers.eventBehavior.Shared;
using canevents.src.EventManagers.events;
using canevents.src.EventPlacesManagers.Places;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace canevents.src.EventManagers.eventBehavior
{
    /// <summary>
    /// Race-mode parkour: all players start together and run ordered checkpoints. Falling below
    /// FallY costs a life and returns the player to their last checkpoint; the first finisher wins,
    /// with a grace window afterwards so stragglers can still record times.
    /// </summary>
    public class ParkourEB : CANEventBehavior, Shared.IMatchScorer
    {
        /// <summary>Undone first thing in OnEnd - see Unsubscribers for why that order matters.</summary>
        private readonly Shared.Unsubscribers subscriptions = new Shared.Unsubscribers();

        private int defaultLives = 3;
        private float finishGraceSeconds = 90f;
        private float prefightSeconds = 3f;
        private float fallY = float.MinValue;
        private int totalCheckpoints; // count of non-finish, non-start checkpoints

        private Dictionary<string, int> lastCheckpointIdx = new Dictionary<string, int>();
        private Dictionary<string, int> livesLeft = new Dictionary<string, int>();
        private Dictionary<string, BlockPos> respawnPos = new Dictionary<string, BlockPos>();
        private Dictionary<string, double> finishTimeMs = new Dictionary<string, double>();
        private HashSet<string> finishedUids = new HashSet<string>();
        private HashSet<string> outOfLivesUids = new HashSet<string>();
        private HashSet<BlockEntities.BlockEntityParkourCheckpoint> activeCheckpointBEs = new HashSet<BlockEntities.BlockEntityParkourCheckpoint>();

        private double startTimeMs;
        private readonly Shared.PrefightTimer prefight = new Shared.PrefightTimer();
        private string firstFinisherUid;
        private bool finalized;
        private long finalizerListenerId = -1;

        public ParkourEB(string name, JsonObject attributes, RunningEvent runningEvent)
            : base(name, attributes, runningEvent) { }

        public override string Name { get => "Parkour"; set { } }

        public override void Initialize()
        {
            defaultLives = Config.Int("lives", 3);
            finishGraceSeconds = Config.Float("finishGraceSeconds", 90f);
            prefightSeconds = Config.Float("prefightSeconds", 3f);
            Config.WarnUnknown();
        }

        public override void OnBeforeStart()
        {
            var place = runningEvent.Place;
            if (place == null)
            {
                log.Error("OnBeforeStart: place is null");
                return;
            }

            if (place.TryGetBehaviorData(BehaviorKeys.Parkour, out var data))
            {
                if (data["FallY"] != null) fallY = data.Value<float>("FallY");
                // Per-place override of lives, falls back to JSON template default.
                if (data["Lives"] != null) defaultLives = data.Value<int>("Lives");

                var arrToken = data["Checkpoints"];

                if (arrToken is Newtonsoft.Json.Linq.JArray arr)
                {
                    int highestIdx = -1;
                    var ba = runningEvent.sapi.World.BlockAccessor;
                    foreach (var t in arr)
                    {
                        if (t is not Newtonsoft.Json.Linq.JObject e) continue;
                        if (!JsonPos.TryRead(e, out var pos)) continue;
                        bool isFin = e.Value<bool>("IsFinish");
                        int idx = e.Value<int>("Index");

                        // Spawn the BE on top of whatever block is already there (typically a
                        // schematic floor block); the block itself is not replaced.
                        if (ba.GetBlockEntity(pos) == null)
                        {
                            ba.SpawnBlockEntity("ParkourCheckpoint", pos);
                        }
                        var existing = ba.GetBlockEntity(pos) as BlockEntities.BlockEntityParkourCheckpoint;
                        if (existing != null)
                        {
                            existing.Index = idx;
                            existing.IsFinish = isFin;
                            existing.MarkDirty(true);
                            existing.StartTicking();
                            activeCheckpointBEs.Add(existing);
                        }

                        if (!isFin && idx > highestIdx) highestIdx = idx;
                    }
                    totalCheckpoints = highestIdx + 1;
                }
            }

            // Default fall threshold: 15 blocks below the lowest spawn point
            if (fallY == float.MinValue)
            {
                int lowest = int.MaxValue;
                if (place.spawnPositions != null)
                {
                    foreach (var sp in place.spawnPositions) if (sp.Y < lowest) lowest = sp.Y;
                }
                if (lowest == int.MaxValue && place.HasBounds) lowest = place.BoundsMin.Y;
                if (lowest != int.MaxValue) fallY = lowest - 15f;
            }
        }

        public override void OnStart()
        {
            prefight.Start(prefightSeconds);
            startTimeMs = runningEvent.sapi.World.ElapsedMilliseconds;

            foreach (var uid in runningEvent.ParticipantsBefore)
            {
                lastCheckpointIdx[uid] = -1;
                livesLeft[uid] = defaultLives;

                var pl = runningEvent.sapi.World.PlayerByUid(uid);
                if (pl != null)
                {
                    // Default respawn = wherever TPThereAndBack just put them
                    respawnPos[uid] = pl.Entity.Pos.AsBlockPos.Copy();
                    EventBroadcaster.Heal(pl);
                }
            }

            if (prefight.Active)
            {
                BroadcastMessage(Lang.Get("canevents:countdown-get-ready", (int)Math.Ceiling(prefightSeconds)));
            }
            else
            {
                BroadcastMessage(Lang.Get("canevents:parkour-go"));
            }

            // Dedicated 250ms finalizer to beat __finishCheck (500ms) reliably even under tick lag.
            finalizerListenerId = runningEvent.RegisterMatchTick(FinalizerTick, 250);

            runningEvent.sapi.Event.BreakBlock += OnBreakBlock;
            subscriptions.Add(() => runningEvent.sapi.Event.BreakBlock -= OnBreakBlock);
        }

        private void OnBreakBlock(IServerPlayer byPlayer, BlockSelection sel, ref float dropMult, ref EnumHandling handling)
        {
            if (sel?.Position == null) return;
            var be = runningEvent.sapi.World.BlockAccessor.GetBlockEntity(sel.Position)
                as BlockEntities.BlockEntityParkourCheckpoint;
            if (be == null) return;
            // Only protect checkpoints that this event actually owns.
            if (!activeCheckpointBEs.Contains(be)) return;
            handling = EnumHandling.PreventDefault;
            dropMult = 0f;
            byPlayer?.SendMessage(0, "Cannot break parkour checkpoints during the event.", EnumChatType.Notification);
        }

        private void FinalizerTick(float dt)
        {
            if (finalized) return;
            if (firstFinisherUid != null
                && runningEvent.ElapsedSeconds + 0.5f >= runningEvent.MaxDurationSeconds)
            {
                FinalizeWinner();
                return;
            }
            bool anyActive = false;
            foreach (var uid in runningEvent.ParticipantsStillInEvent)
            {
                if (!finishedUids.Contains(uid) && !outOfLivesUids.Contains(uid))
                {
                    anyActive = true;
                    break;
                }
            }
            if (!anyActive && (finishedUids.Count > 0 || runningEvent.ParticipantsStillInEvent.Count == 0))
            {
                FinalizeWinner();
            }
        }

        public override void OnTick(float dt)
        {
            if (prefight.Tick(dt,
                    second => BroadcastMessage(Lang.Get("canevents:countdown-tick", second)),
                    () =>
                    {
                        // The clock starts when the race does, not when the countdown began.
                        startTimeMs = runningEvent.sapi.World.ElapsedMilliseconds;
                        BroadcastMessage(Lang.Get("canevents:parkour-go"));
                    }))
                return;

            // Fall detection (finalization handled by the 250ms FinalizerTick listener).
            // Iterate a copy: HandleFall eliminates the player, mutating the set being walked -
            // don't rely on the roster's current backing type tolerating that.
            foreach (var uid in runningEvent.ParticipantsStillInEvent.ToArray())
            {
                if (finishedUids.Contains(uid)) continue;
                if (outOfLivesUids.Contains(uid)) continue;
                var pl = runningEvent.sapi.World.PlayerByUid(uid) as IServerPlayer;
                if (pl == null) continue;
                // Skip a corpse: a death below fallY is already handled by OnParticipantDeath;
                // otherwise HandleFall fires on the body too and costs a SECOND life.
                if (!pl.Entity.Alive) continue;
                if (pl.Entity.Pos.Y < fallY) HandleFall(pl);
            }
        }

        private void HandleFall(IServerPlayer player)
        {
            string uid = player.PlayerUID;
            if (!livesLeft.TryGetValue(uid, out var lives)) return;
            lives--;
            livesLeft[uid] = lives;

            if (lives <= 0)
            {
                outOfLivesUids.Add(uid);
                BroadcastMessage(Lang.Get("canevents:parkour-out-of-lives", player.PlayerName));
                if (runningEvent.NetworkHandler != null)
                    runningEvent.NetworkHandler.SendKillFeed(runningEvent, "the void", player.PlayerName, "fell");
                runningEvent.EliminatePlayer(uid);
                runningEvent.Spectator?.MakeSpectator(player, runningEvent);
                return;
            }

            if (respawnPos.TryGetValue(uid, out var rp) && rp != null)
            {
                player.Entity.TeleportToDouble(rp.X + 0.5, rp.Y + 0.5, rp.Z + 0.5);
                EventBroadcaster.Heal(player);
            }
            player.SendMessage(0, Lang.Get("canevents:parkour-lives-left", lives), EnumChatType.Notification);
        }

        /// <summary>Called from the checkpoint block entity when a participant enters its trigger area.</summary>
        public void OnPlayerHitCheckpoint(IServerPlayer player, BlockPos cpPos, int index, bool isFinish)
        {
            if (prefight.Active) return;
            if (player == null) return;
            string uid = player.PlayerUID;
            if (!runningEvent.IsActive(uid)) return;
            if (finishedUids.Contains(uid)) return;
            if (outOfLivesUids.Contains(uid)) return;

            if (isFinish)
            {
                if (!lastCheckpointIdx.TryGetValue(uid, out var li)) return;
                if (totalCheckpoints > 0 && li < totalCheckpoints - 1) return; // skipped checkpoints
                HandleFinish(player);
                return;
            }

            if (!lastCheckpointIdx.TryGetValue(uid, out var last)) return;
            if (index != last + 1) return; // out-of-order or already passed

            lastCheckpointIdx[uid] = index;
            // Respawn one block above the checkpoint so the player lands on top, not inside.
            if (cpPos != null) respawnPos[uid] = cpPos.UpCopy();

            int total = totalCheckpoints > 0 ? totalCheckpoints : (index + 1);
            BroadcastMessage(Lang.Get("canevents:parkour-checkpoint", player.PlayerName, index + 1, total));
        }

        private void HandleFinish(IServerPlayer player)
        {
            string uid = player.PlayerUID;
            finishedUids.Add(uid);
            double t = runningEvent.sapi.World.ElapsedMilliseconds - startTimeMs;
            finishTimeMs[uid] = t;

            BroadcastMessage(Lang.Get("canevents:parkour-finished", player.PlayerName, t / 1000.0));

            if (firstFinisherUid == null)
            {
                firstFinisherUid = uid;

                // The rest get a grace window, not the full time limit. ShortenDuration refuses to
                // extend, so a grace longer than what remains is a silent no-op.
                float graceCap = runningEvent.ElapsedSeconds + finishGraceSeconds;
                if (runningEvent.ShortenDuration(graceCap, "parkour grace window after first finisher"))
                    BroadcastMessage(Lang.Get("canevents:parkour-grace", (int)finishGraceSeconds));
            }

            // Spectate finishers so they stop running the course, but keep them in
            // ParticipantsStillInEvent so FinalizeWinner can pick the winner.
            runningEvent.Spectator?.MakeSpectator(player, runningEvent);
        }

        private void FinalizeWinner()
        {
            if (finalized) return;
            finalized = true;

            // Fastest finisher (in race mode, the first one).
            string winnerUid = null;
            double bestTime = double.MaxValue;
            foreach (var uid in finishedUids)
            {
                if (finishTimeMs.TryGetValue(uid, out var t) && t < bestTime)
                {
                    bestTime = t;
                    winnerUid = uid;
                }
            }

            if (winnerUid != null)
            {
                var winner = runningEvent.sapi.World.PlayerByUid(winnerUid);
                BroadcastMessage(Lang.Get("canevents:parkour-winner",
                    winner?.PlayerName ?? winnerUid, bestTime / 1000.0));
            }
            else
            {
                BroadcastMessage(Lang.Get("canevents:parkour-no-finishers"));
            }

            // Eliminate everyone except the winner so RunningEvent.GetWinners() returns the right group.
            var snapshot = new List<string>(runningEvent.ParticipantsStillInEvent);
            foreach (var uid in snapshot)
            {
                if (uid != winnerUid) runningEvent.EliminatePlayer(uid);
            }
            // EliminatePlayer requests a finish on 0/1 groups remaining, but in single-player nobody
            // is eliminated, so ask explicitly. RequestFinish is idempotent.
            runningEvent.RequestFinish("parkour finished");
        }

        public override void OnEnd()
        {
            // Teardown FIRST, cosmetics after: a throw in the ranking broadcast is swallowed per
            // behavior and would otherwise leak the BreakBlock subscription and checkpoint BEs.
            subscriptions.UnhookAll();

            // Remove the BEs attached on top of existing world blocks; nothing else cleans them up,
            // since we don't own the underlying block.
            var ba = runningEvent.sapi.World.BlockAccessor;
            foreach (var be in activeCheckpointBEs)
            {
                if (be == null) continue;
                be.StopTicking();
                ba.RemoveBlockEntity(be.Pos);
            }
            activeCheckpointBEs.Clear();

            if (finishTimeMs.Count > 0)
            {
                var ordered = new List<KeyValuePair<string, double>>(finishTimeMs);
                ordered.Sort((a, b) => a.Value.CompareTo(b.Value));
                BroadcastMessage(Lang.Get("canevents:parkour-times-header"));
                int rank = 1;
                foreach (var kvp in ordered)
                {
                    var pl = runningEvent.sapi.World.PlayerByUid(kvp.Key);
                    BroadcastMessage(Lang.Get("canevents:parkour-times-row",
                        rank++, pl?.PlayerName ?? kvp.Key, kvp.Value / 1000.0));
                }
            }

            lastCheckpointIdx.Clear();
            livesLeft.Clear();
            respawnPos.Clear();
            finishTimeMs.Clear();
            finishedUids.Clear();
            outOfLivesUids.Clear();
        }

        // IMatchScorer (parkour has no kills/deaths)
        public Dictionary<string, int> GetFinalKillCounts() => null;
        public Dictionary<string, int> GetFinalDeathCounts() => null;

        /// <summary>Placement: finishers by time, then non-finishers by checkpoints reached.
        /// Each player is their own bracket.</summary>
        public List<HashSet<string>> GetFinalRanking()
        {
            var finishers = runningEvent.ParticipantsBefore
                .Where(uid => finishTimeMs.ContainsKey(uid))
                .OrderBy(uid => finishTimeMs[uid]);
            var rest = runningEvent.ParticipantsBefore
                .Where(uid => !finishTimeMs.ContainsKey(uid))
                .OrderByDescending(uid => lastCheckpointIdx.TryGetValue(uid, out var c) ? c : 0);
            return finishers.Concat(rest)
                .Select(uid => new HashSet<string> { uid })
                .ToList();
        }

        public override void OnParticipantDeath(IServerPlayer player, DamageSource source)
        {
            // Real death also costs a life. Vanilla respawn fires after this, and
            // OnParticipantRespawn teleports them back to the last checkpoint if lives remain.
            string uid = player.PlayerUID;
            if (outOfLivesUids.Contains(uid)) return;
            if (finishedUids.Contains(uid)) return;
            if (!livesLeft.TryGetValue(uid, out var lives)) return;

            lives--;
            livesLeft[uid] = lives;

            if (lives <= 0)
            {
                outOfLivesUids.Add(uid);
                if (runningEvent.IsActive(uid))
                {
                    runningEvent.EliminatePlayer(uid);
                    // CANEventManager.OnDeath then sees them out of ParticipantsStillInEvent and
                    // calls MakeSpectator + RecordDeath after this returns.
                }
                return;
            }
            player.SendMessage(0, Lang.Get("canevents:parkour-lives-left", lives), EnumChatType.Notification);
        }

        public override void OnParticipantRespawn(IServerPlayer player)
        {
            // Reroute respawn to the last checkpoint (finishers/out-of-lives are skipped).
            string uid = player.PlayerUID;
            if (!runningEvent.IsActive(uid)) return;
            if (finishedUids.Contains(uid) || outOfLivesUids.Contains(uid)) return;
            if (respawnPos.TryGetValue(uid, out var rp) && rp != null)
            {
                player.Entity.TeleportToDouble(rp.X + 0.5, rp.Y + 0.5, rp.Z + 0.5);
            }
        }

        private void BroadcastMessage(string message)
            => EventBroadcaster.Broadcast(runningEvent, message);
    }
}
