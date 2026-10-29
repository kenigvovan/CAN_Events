using System;
using System.Collections.Generic;
using System.Linq;
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
using Vintagestory.GameContent;

namespace canevents.src.EventManagers.eventBehavior
{
    // Race parkour: ordered checkpoints, lives lost below FallY, first finisher starts a grace window.
    public class ParkourEB : CANEventBehavior, Shared.IMatchScorer
    {
        // Released first in OnEnd.
        private readonly Shared.ArenaBlockGuard blockGuard = new Shared.ArenaBlockGuard();

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
        // Keyed by floor block. Detection is in CheckpointTick only; BEs are passive markers.
        private readonly Dictionary<string, (BlockPos pos, int index, bool isFinish)> checkpoints
            = new Dictionary<string, (BlockPos, int, bool)>();
        private readonly HashSet<BlockPos> markerPositions = new HashSet<BlockPos>();
        private const int CheckpointScanMs = 200;

        private double startTimeMs;
        private readonly Shared.PrefightTimer prefight = new Shared.PrefightTimer();
        private string firstFinisherUid;
        private bool finalized;
        private long finalizerListenerId = -1;

        public ParkourEB(string name, JsonObject attributes, RunningEvent runningEvent)
            : base(name, attributes, runningEvent) { }

        public override string Name => BehaviorNames.Parkour;

        public override void Initialize()
        {
            defaultLives = Config.Int("lives", 3);
            finishGraceSeconds = Config.Float("finishGraceSeconds", 90f);
            prefightSeconds = Shared.PrefightTimer.ReadSeconds(Config);
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
                if (data["Lives"] != null) defaultLives = data.Value<int>("Lives");

                var arrToken = data["Checkpoints"];

                if (arrToken is Newtonsoft.Json.Linq.JArray arr)
                {
                    // Stored indices are only an order (may start at 1 or have gaps); renumber to 0..n-1.
                    var marked = new List<(BlockPos pos, bool isFin, int stored)>();
                    foreach (var t in arr)
                    {
                        if (t is not Newtonsoft.Json.Linq.JObject e) continue;
                        if (!JsonPos.TryRead(e, out var p)) continue;
                        marked.Add((p, e.Value<bool>("IsFinish"), e.Value<int>("Index")));
                    }
                    var ordered = marked.Where(m => !m.isFin).OrderBy(m => m.stored)
                        .Select((m, seq) => (m.pos, isFin: false, idx: seq))
                        .Concat(marked.Where(m => m.isFin).Select(m => (m.pos, isFin: true, idx: 0)));

                    checkpoints.Clear();
                    markerPositions.Clear();
                    var ba = runningEvent.sapi.World.BlockAccessor;
                    foreach (var (pos, isFin, idx) in ordered)
                    {
                        checkpoints[CheckpointKey(pos)] = (pos.Copy(), idx, isFin);

                        // Marker BE only; the block stays, and an existing BE is never replaced.
                        if (ba.GetBlockEntity(pos) == null) ba.SpawnBlockEntity("ParkourCheckpoint", pos);
                        if (ba.GetBlockEntity(pos) is BlockEntities.BlockEntityParkourCheckpoint marker)
                        {
                            marker.Index = idx;
                            marker.IsFinish = isFin;
                            marker.MarkDirty(true);
                            markerPositions.Add(pos.Copy());
                        }
                    }
                    totalCheckpoints = marked.Count(m => !m.isFin);
                }
            }

            // Default: 15 blocks below the lowest spawn.
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
            prefight.Start(prefightSeconds, runningEvent);
            startTimeMs = runningEvent.sapi.World.ElapsedMilliseconds;

            foreach (var uid in runningEvent.ParticipantsBefore)
            {
                lastCheckpointIdx[uid] = -1;
                livesLeft[uid] = defaultLives;

                var pl = runningEvent.sapi.World.PlayerByUid(uid);
                if (pl != null) EventBroadcaster.Heal(pl);
                // Start position is captured later in CaptureStartPositions: the teleport is async.
            }

            if (!prefight.Active) Announce("canevents:parkour-go");

            // 250ms so it runs before __finishCheck (500ms) even under lag.
            finalizerListenerId = runningEvent.RegisterMatchTick(FinalizerTick, 250);
            runningEvent.RegisterMatchTick(CheckpointTick, CheckpointScanMs);

            blockGuard.GuardBreaks(runningEvent, JudgeCheckpointBreak,
                "canevents-parkour-cp", "canevents:parkour-no-break-checkpoint");
        }

        private Shared.BlockBreakVerdict JudgeCheckpointBreak(IServerPlayer byPlayer, BlockPos pos)
            => checkpoints.ContainsKey(CheckpointKey(pos))
                ? Shared.BlockBreakVerdict.Keep
                : Shared.BlockBreakVerdict.NotGuarded;

        private static string CheckpointKey(BlockPos pos, int dy = 0)
            => pos.X + "," + (pos.Y + dy) + "," + pos.Z + "," + pos.dimension;

        // Finished or out of lives: nothing left to race for.
        private bool OutOfRace(string uid) => finishedUids.Contains(uid) || outOfLivesUids.Contains(uid);

        // Checkpoint is the block under the player's feet.
        private void CheckpointTick(float dt)
        {
            if (prefight.Active || finalized || checkpoints.Count == 0) return;

            // Copy: finishing can eliminate players from the set.
            foreach (var uid in runningEvent.ParticipantsStillInEvent.ToArray())
            {
                if (OutOfRace(uid)) continue;
                var player = runningEvent.sapi.World.PlayerByUid(uid) as IServerPlayer;
                var entity = player?.Entity;
                if (entity == null || !entity.Alive) continue;

                if (checkpoints.TryGetValue(CheckpointKey(entity.Pos.AsBlockPos, -1), out var cp))
                    OnPlayerHitCheckpoint(player, cp.pos, cp.index, cp.isFinish);
            }
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
            bool anyActive = runningEvent.ParticipantsStillInEvent.Any(uid => !OutOfRace(uid));
            if (!anyActive && (finishedUids.Count > 0 || runningEvent.ParticipantsStillInEvent.Count == 0))
            {
                FinalizeWinner();
            }
        }

        // Records start once the player has landed near a spawn.
        private void CaptureStartPositions()
        {
            if (respawnPos.Count >= runningEvent.ParticipantsBefore.Count) return;
            var spawns = runningEvent.Place?.spawnPositions;
            foreach (var uid in runningEvent.ParticipantsBefore)
            {
                if (respawnPos.ContainsKey(uid)) continue;
                var entity = runningEvent.sapi.World.PlayerByUid(uid)?.Entity;
                if (entity == null || entity.Teleporting) continue;
                var here = entity.Pos.AsBlockPos;
                // within 3 blocks of a spawn; no spawns = accept any position
                bool arrived = spawns == null || spawns.Count == 0
                    || spawns.Any(s => s.HorDistanceSqTo(here.X, here.Z) <= 9 && Math.Abs(s.Y - here.Y) <= 3);
                if (arrived) respawnPos[uid] = here.Copy();
            }
        }

        private BlockPos StartFallback()
        {
            var spawns = runningEvent.Place?.spawnPositions;
            return spawns != null && spawns.Count > 0 ? spawns[0].Copy() : null;
        }

        private BlockPos RespawnFor(string uid)
            => respawnPos.TryGetValue(uid, out var rp) && rp != null ? rp : StartFallback();

        public override bool WantsTick => true;

        public override void OnTick(float dt)
        {
            CaptureStartPositions();   // also during the countdown, teleports land then

            if (prefight.Tick(dt, () =>
                {
                    startTimeMs = runningEvent.sapi.World.ElapsedMilliseconds;
                    Announce("canevents:parkour-go");
                }))
                return;

            // Copy: HandleFall can remove the player from the set.
            foreach (var uid in runningEvent.ParticipantsStillInEvent.ToArray())
            {
                if (OutOfRace(uid)) continue;
                var pl = runningEvent.sapi.World.PlayerByUid(uid) as IServerPlayer;
                if (pl == null) continue;
                // Dead bodies are handled by OnParticipantDeath, else it costs two lives.
                if (!pl.Entity.Alive) continue;
                if (Shared.VoidFall.HasFallen(pl.Entity, fallY)) HandleFall(pl);
            }
        }

        // null if not in the race. At zero: RemoveFromPlay, not EliminatePlayer (last one left isn't a win here).
        private int? LoseLife(string uid)
        {
            if (!livesLeft.TryGetValue(uid, out var lives)) return null;
            livesLeft[uid] = --lives;
            if (lives <= 0)
            {
                outOfLivesUids.Add(uid);
                if (runningEvent.IsActive(uid)) runningEvent.RemoveFromPlay(uid);
            }
            return lives;
        }

        private void HandleFall(IServerPlayer player)
        {
            string uid = player.PlayerUID;
            if (!(LoseLife(uid) is int lives)) return;

            if (lives <= 0)
            {
                Announce("canevents:parkour-out-of-lives", player.PlayerName);
                Shared.VoidFall.AnnounceInFeed(runningEvent, player.PlayerName, "fell");
                runningEvent.SeatOutOfPlay(player);
                return;
            }

            if (SendToRespawn(player, "parkour checkpoint")) EventBroadcaster.Heal(player);
            player.SendMessage(0, Lang.Get("canevents:parkour-lives-left", lives), EnumChatType.Notification);
        }

        private void OnPlayerHitCheckpoint(IServerPlayer player, BlockPos cpPos, int index, bool isFinish)
        {
            if (prefight.Active) return;
            if (player == null) return;
            string uid = player.PlayerUID;
            if (!runningEvent.IsActive(uid) || OutOfRace(uid)) return;

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
            if (cpPos != null) respawnPos[uid] = cpPos.UpCopy();

            int total = totalCheckpoints > 0 ? totalCheckpoints : (index + 1);
            Announce("canevents:parkour-checkpoint", player.PlayerName, index + 1, total);
        }

        private void HandleFinish(IServerPlayer player)
        {
            string uid = player.PlayerUID;
            finishedUids.Add(uid);
            double t = runningEvent.sapi.World.ElapsedMilliseconds - startTimeMs;
            finishTimeMs[uid] = t;

            Announce("canevents:parkour-finished", player.PlayerName, t / 1000.0);

            if (firstFinisherUid == null)
            {
                firstFinisherUid = uid;

                // ShortenDuration never extends, so a long grace is a no-op.
                float graceCap = runningEvent.ElapsedSeconds + finishGraceSeconds;
                if (runningEvent.ShortenDuration(graceCap, "parkour grace window after first finisher"))
                    Announce("canevents:parkour-grace", (int)finishGraceSeconds);
            }

            // Finishers stay in ParticipantsStillInEvent for FinalizeWinner.
            runningEvent.SeatOutOfPlay(player);
        }

        private void FinalizeWinner()
        {
            if (finalized) return;
            finalized = true;

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

            if (winnerUid != null) Announce("canevents:parkour-winner", runningEvent.NameOf(winnerUid), bestTime / 1000.0);
            else Announce("canevents:parkour-no-finishers");

            // So GetWinners() returns just the winner.
            var snapshot = new List<string>(runningEvent.ParticipantsStillInEvent);
            foreach (var uid in snapshot)
            {
                if (uid != winnerUid) runningEvent.EliminatePlayer(uid);
            }
            // Solo races eliminate nobody, so request explicitly (idempotent).
            runningEvent.RequestFinish("parkour finished");
        }

        public override void OnEnd()
        {
            // Teardown before announcements, so a throw there can't leak the subscription or BEs.
            blockGuard.Release();

            var ba = runningEvent.sapi.World.BlockAccessor;
            foreach (var pos in markerPositions)
                if (ba.GetBlockEntity(pos) is BlockEntities.BlockEntityParkourCheckpoint)
                    ba.RemoveBlockEntity(pos);
            markerPositions.Clear();
            checkpoints.Clear();

            if (finishTimeMs.Count > 0)
            {
                var ordered = new List<KeyValuePair<string, double>>(finishTimeMs);
                ordered.Sort((a, b) => a.Value.CompareTo(b.Value));
                Announce("canevents:parkour-times-header");
                int rank = 1;
                foreach (var kvp in ordered)
                    Announce("canevents:parkour-times-row", rank++, runningEvent.NameOf(kvp.Key), kvp.Value / 1000.0);
            }

            lastCheckpointIdx.Clear();
            livesLeft.Clear();
            respawnPos.Clear();
            finishTimeMs.Clear();
            finishedUids.Clear();
            outOfLivesUids.Clear();
        }

        // Finishers by time, then the rest by checkpoints reached.
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

        public override void OnParticipantDeath(string uid, Entity body, DamageSource source)
        {
            if (OutOfRace(uid)) return;
            if (!(LoseLife(uid) is int lives)) return;

            // CANEventManager.OnDeath makes them a spectator after this returns.
            if (lives <= 0) return;
            (runningEvent.sapi.World.PlayerByUid(uid) as IServerPlayer)?.SendMessage(0,
                Lang.Get("canevents:parkour-lives-left", lives), EnumChatType.Notification);
        }

        public override void OnParticipantRespawn(IServerPlayer player)
        {
            string uid = player.PlayerUID;
            if (!runningEvent.IsActive(uid)) return;
            if (OutOfRace(uid)) return;
            SendToRespawn(player, "parkour respawn");
        }

        private bool SendToRespawn(IServerPlayer player, string reason)
        {
            var rp = RespawnFor(player.PlayerUID);
            if (rp == null) return false;
            EventTeleport.Send(runningEvent.sapi, player, rp, 0.5, 0.5, 0.5, reason);
            return true;
        }
    }
}
