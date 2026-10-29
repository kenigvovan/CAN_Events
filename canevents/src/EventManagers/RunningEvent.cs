using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using canevents.src.EventManagers.events;
using canevents.src.EventManagers.eventBehavior.Shared;
using canevents.src.EventPlacesManagers.Places;
using canevents.src.Network;
using canevents.src.QueueManagers;
using Vintagestory.API.Server;
using canevents.src.Bots;

namespace canevents.src.EventManagers
{
    public enum EventState
    {
        SearchingForLocation,
        LocationFound,
        Running,
        Finished,

        // Ready check, before the arena search. Appended last: the int value goes over the wire in EventStateUpdateMsg.
        AwaitingReady
    }
    public class RunningEvent
    {
        // Persisted so ids don't collide with stale client state after a restart.
        private const string IdCounterSaveKey = "canevents_event_id_counter";
        private static long lastId = 0;

        public static void SeedIdCounter(ICoreServerAPI sapi)
        {
            try
            {
                byte[] data = sapi.WorldManager.SaveGame.GetData(IdCounterSaveKey);
                if (data != null && data.Length >= 8)
                {
                    long saved = BitConverter.ToInt64(data, 0);
                    if (saved > Interlocked.Read(ref lastId))
                    {
                        Interlocked.Exchange(ref lastId, saved);
                    }
                }
            }
            catch (Exception e)
            {
                sapi.World.Logger.Warning("RunningEvent.SeedIdCounter failed: {0}", e.Message);
            }
        }

        // Reads the current max so interleaved calls still save the largest id.
        private static void PersistIdCounter(ICoreServerAPI sapi)
        {
            try
            {
                long current = Interlocked.Read(ref lastId);
                sapi.WorldManager.SaveGame.StoreData(IdCounterSaveKey, BitConverter.GetBytes(current));
            }
            catch (Exception e)
            {
                sapi.World.Logger.Warning("RunningEvent.PersistIdCounter failed: {0}", e.Message);
            }
        }

        public CANEvent Event { get; set; }
        public List<CANEventBehavior> Behaviors { get; private set; }

        public ArenaBotManager Bots => bots ??= new ArenaBotManager(this);
        private ArenaBotManager bots;

        // HG truce. Players are held by damage hooks, bots check this flag.
        public bool CombatSuspended { get; set; }

        // Tournament arena whitelist; null/empty = any arena.
        public HashSet<long> AllowedPlaceIds { get; set; }

        // Tournament owns the pot.
        public bool SuppressEntryFeeSettlement { get; set; }

        public bool SuppressRating { get; set; }

        // Tournament sub-match; the bracket pays prizes once at the end.
        public bool SuppressRewards { get; set; }

        public List<Vintagestory.API.MathTools.Vec2i> PinnedChunks { get; set; }

        // Pedestal freeze. Separate from CombatSuspended: bots may walk during the truce.
        public bool MovementFrozen { get; set; }

        // HUD shows a hold countdown until ElapsedSeconds reaches this. 0 = none.
        public float HoldEndsAtElapsed { get; private set; }
        public string HoldLangKey { get; private set; }

        // Sent with the next state update.
        public void BeginHold(float seconds, string langKey)
        {
            if (seconds <= 0f) return;
            HoldEndsAtElapsed = ElapsedSeconds + seconds;
            HoldLangKey = langKey;
        }

        // Re-syncs the HUD so the start cry doesn't depend on the client clock.
        public void EndHold()
        {
            if (HoldEndsAtElapsed <= 0f) return;
            HoldEndsAtElapsed = 0f;
            HoldLangKey = null;
            NetworkHandler?.SendStateUpdate(this);
        }

        // Player or bot entity. Use instead of PlayerByUid, which skips bots.
        public Vintagestory.API.Common.Entities.Entity BodyOf(string uid)
        {
            if (uid == null) return null;
            if (BotIdentity.IsBot(uid)) return Bots.EntityOf(uid);
            return (sapi.World.PlayerByUid(uid) as IServerPlayer)?.Entity;
        }

        // Iterates a copy, safe to eliminate inside the loop.
        public IEnumerable<(string uid, Vintagestory.API.Common.Entities.Entity body)> LiveBodies()
        {
            foreach (var uid in ParticipantsStillInEvent.ToArray())
            {
                var body = BodyOf(uid);
                if (body != null && body.Alive) yield return (uid, body);
            }
        }

        public string NameOf(string uid) => NameOf(sapi, uid);

        public static string NameOf(ICoreServerAPI api, string uid)
            => BotIdentity.IsBot(uid)
                ? BotIdentity.NameOfUid(uid)
                : api?.World.PlayerByUid(uid)?.PlayerName ?? uid;

        // Over the per-match clones.
        public T GetBehavior<T>() where T : class => CANEvent.FirstBehavior<T>(Behaviors);

        public IMatchScorer Scorer { get; private set; }
        public MatchRoster Roster { get; } = new MatchRoster();

        public IReadOnlyList<string> ParticipantsBefore => Roster.ParticipantsBefore;
        public IReadOnlyList<HashSet<string>> ParticipantsByGroupsBefore => Roster.ParticipantsByGroupsBefore;
        public IReadOnlyCollection<string> ParticipantsStillInEvent => Roster.ParticipantsStillInEvent;
        public IReadOnlyList<HashSet<string>> ParticipantsByGroupsStillInEvent => Roster.ParticipantsByGroupsStillInEvent;

        // Skips bots and offline players.
        public IEnumerable<IServerPlayer> OnlineParticipants
        {
            get
            {
                foreach (var uid in ParticipantsBefore)
                    if (sapi.World.PlayerByUid(uid) is IServerPlayer player) yield return player;
            }
        }

        // Same, minus pre-start dropouts and the eliminated.
        public IEnumerable<IServerPlayer> ActiveOnlineParticipants
        {
            get
            {
                foreach (var player in OnlineParticipants)
                    if (IsActive(player.PlayerUID)) yield return player;
            }
        }

        // Admin observers, kept out of MatchRoster so they never count for winners/rewards/pot.
        public HashSet<string> Observers { get; } = new HashSet<string>();

        // Recipients of every HUD packet: full starting line-up plus observers.
        public IEnumerable<IServerPlayer> HudAudience
        {
            get
            {
                foreach (var uid in ParticipantsBefore)
                    if (sapi.World.PlayerByUid(uid) is IServerPlayer player) yield return player;

                foreach (var uid in Observers)
                {
                    if (Roster.IsParticipant(uid)) continue;
                    if (sapi.World.PlayerByUid(uid) is IServerPlayer observer) yield return observer;
                }
            }
        }

        // No-op once running, use EliminatePlayer then.
        public void MarkPreStartDropout(string uid) => Roster.MarkPreStartDropout(uid);

        public void InitializeRoster() => Roster.Initialize();

        public int IndexInStartingLineup(string uid) => Roster.IndexInStartingLineup(uid);

        // Off by default: CTF flag posts are indexed by team, so spawn order matters there.
        private bool shuffleSpawns;
        private int[] spawnOrder;

        // Call in OnBeforeStart, before the first spawn is resolved. Order is drawn once per match.
        public void EnableSpawnShuffle() => shuffleSpawns = true;

        public int MapSpawnIndex(int index)
        {
            if (!shuffleSpawns || index < 0) return index;
            spawnOrder ??= BuildSpawnOrder();
            return spawnOrder != null && index < spawnOrder.Length ? spawnOrder[index] : index;
        }

        // Permutes whole team runs (see TPThereAndBack.ResolveSpawn) so teammates stay together.
        private int[] BuildSpawnOrder()
        {
            int count = Place?.spawnPositions?.Count ?? 0;
            if (count < 2) return null;

            int run = TeamRunLength(count);
            int runs = count / run;
            if (runs < 2) return null;

            var perm = new int[runs];
            for (int i = 0; i < runs; i++) perm[i] = i;
            var rand = sapi.World.Rand;
            for (int i = runs - 1; i > 0; i--)          // Fisher-Yates
            {
                int j = rand.Next(i + 1);
                (perm[i], perm[j]) = (perm[j], perm[i]);
            }

            var order = new int[count];
            for (int i = 0; i < count; i++)
            {
                int r = i / run;
                // Leftover tail keeps its index.
                order[i] = r < runs ? perm[r] * run + i % run : i;
            }
            return order;
        }

        // >1 only for equal teams with a spawn point per player.
        private int TeamRunLength(int spawnCount)
        {
            var groups = ParticipantsByGroupsBefore;
            if (groups == null || groups.Count == 0) return 1;

            int size = groups[0].Count;
            int total = size;
            for (int i = 1; i < groups.Count; i++)
            {
                if (groups[i].Count != size) return 1;
                total += groups[i].Count;
            }

            return spawnCount >= total ? Math.Max(1, size) : 1;
        }
        public HashSet<string> ReadyUids { get; } = new HashSet<string>();

        // 0 = no ready window.
        public long ReadyDeadlineMs { get; set; }

        // Picked up by the manager on its next tick.
        public bool ShouldFinish { get; private set; }

        // First reason wins.
        public void RequestFinish(string reason)
        {
            if (ShouldFinish) return;
            ShouldFinish = true;
            sapi?.World?.Logger?.VerboseDebug("[canevents] Event {0} ({1}) finishing: {2}",
                Id, Event?.Code, reason ?? "unspecified");
        }
        public float MaxDurationSeconds { get; private set; } = 300f;

        // No time limit at all (open matches). SetDuration can't express it: it ignores <= 0.
        public bool Unlimited { get; set; }

        // Players come and go while it runs; never finishes on its own, unrated, no rewards.
        public bool IsOpenMatch { get; set; }

        // Every member of a team spawns at its team's point, whatever the spawn count.
        public bool PerTeamSpawns { get; set; }

        // Lobby side picks, uid -> team index.
        public Dictionary<string, int> TeamChoices { get; } = new Dictionary<string, int>();

        public void SetDuration(float seconds)
        {
            if (seconds > 0f) MaxDurationSeconds = seconds;
        }

        // Parkour grace countdown. Never extends.
        public bool ShortenDuration(float seconds, string reason)
        {
            if (seconds <= 0f || seconds >= MaxDurationSeconds) return false;

            MaxDurationSeconds = seconds;
            sapi?.World?.Logger?.VerboseDebug("[canevents] Event {0} ({1}) shortened to {2}s: {3}",
                Id, Event?.Code, seconds, reason ?? "unspecified");
            return true;
        }
        public float ElapsedSeconds { get; set; } = 0f;
        public float CountdownSeconds { get; set; } = 0f;
        // For the arena search timeout.
        public float SearchingSeconds { get; set; } = 0f;
        public EventPlace Place { get; set; }
        public EventState EventState { get; set; }
        public long Id { get; set; } = 0;
        public ICoreServerAPI sapi { get; private set; }

        public MatchServices Services { get; private set; }

        public Network.ServerNetworkHandler  NetworkHandler => Services?.NetworkHandler;
        public Scoring.LeaderboardData       Leaderboard    => Services?.Leaderboard;
        public LoadoutManager                LoadoutManager => Services?.LoadoutManager;
        public LoadoutManager                OutfitManager  => Services?.OutfitManager;
        public SpectatorManager              Spectator      => Services?.Spectator;
        public Config                        Config         => Services?.Config;

        // Behaviors must go through this instead of seating players themselves.
        public void SeatOutOfPlay(IServerPlayer player) => Services?.SeatOutOfPlay?.Invoke(player, this);
        // Check before issuing anything: nothing is restored, so issued kit would leave the arena.
        public bool KeepOwnGear
        {
            get
            {
                if (Behaviors == null) return false;
                foreach (var b in Behaviors)
                    if (b is eventBehavior.SavePlayerAttributes save && save.KeepOwnGear) return true;
                return false;
            }
        }

        // Unregistered in FinishEvent. A list, so behaviors with the same Name don't clash.
        public List<long> RegisteredTickListeners = new List<long>();

        public long RegisterMatchTick(Action<float> onTick, int intervalMs)
        {
            long id = sapi.Event.RegisterGameTickListener(dt => onTick(dt), intervalMs);
            RegisteredTickListeners.Add(id);
            return id;
        }

        public void UnregisterMatchTick(long id)
        {
            if (id < 0) return;
            if (!RegisteredTickListeners.Remove(id)) return;
            sapi?.Event.UnregisterGameTickListener(id);
        }
        // Separate: unregistered mid-life when the countdown elapses.
        public long CountdownListenerId = -1;
        public bool IsActive(string uid) => Roster.IsActive(uid);

        // Stays true after elimination.
        public bool IsParticipant(string uid) => Roster.IsParticipant(uid);

        // Sent home early with their own gear.
        private readonly HashSet<string> releasedUids = new HashSet<string>();

        public void MarkReleased(string uid)
        {
            if (!string.IsNullOrEmpty(uid)) releasedUids.Add(uid);
        }

        // Open-match rejoin.
        public void ClearReleased(string uid)
        {
            if (!string.IsNullOrEmpty(uid)) releasedUids.Remove(uid);
        }

        // False once released or a pre-start dropout: the match must not clear their inventory anymore.
        public bool HoldsPlayer(string uid)
            => IsParticipant(uid) && !releasedUids.Contains(uid) && !Roster.IsPreStartDropout(uid);

        // Quit mid-match, no participation reward.
        private readonly HashSet<string> leftInPlayUids = new HashSet<string>();

        public void MarkLeftInPlay(string uid)
        {
            if (!string.IsNullOrEmpty(uid)) leftInPlayUids.Add(uid);
        }

        public bool LeftInPlay(string uid) => leftInPlayUids.Contains(uid);

        public int TeamIndexOf(string uid) => Roster.TeamIndexOf(uid);

        public Dictionary<string, string> PlayerLoadoutChoices = new Dictionary<string, string>();
        // No loadout popup for these.
        public HashSet<string> ExplicitLoadoutChoiceUids = new HashSet<string>();
        // Party registrants already picked, no popup either.
        public HashSet<string> RegistrantUids = new HashSet<string>();
        public RunningEvent()
        {
        }

        public RunningEvent(ICoreServerAPI api, CANEvent canEvent, TeamAssembly ta, MatchServices services = null)
        {
            this.sapi     = api;
            this.Services = services;
            this.Event    = canEvent;
            this.Behaviors = new List<CANEventBehavior>();
            foreach (var behavior in canEvent.Behaviors)
            {
                var clone = behavior.Clone();
                clone.runningEvent = this;
                clone.Initialize();
                this.Behaviors.Add(clone);
            }
            var scorers = Behaviors.OfType<IMatchScorer>().ToList();
            if (scorers.Count > 1)
                api.World.Logger.Warning("RunningEvent ({0}): {1} IMatchScorer behaviors - using the first.", canEvent.Code, scorers.Count);
            Scorer = scorers.Count > 0 ? scorers[0] : null;

            Roster.SetStartingLineup(ta.GetPlayerGuids(), ta.GetPlayerGuidsGroups());
            this.EventState = EventState.SearchingForLocation;

            // Registrant's pick is the party fallback.
            foreach (var shard in ta.Shards)
            {
                foreach (var reg in shard.Registrations)
                {
                    if (!string.IsNullOrEmpty(reg.RegistrantUid))
                    {
                        RegistrantUids.Add(reg.RegistrantUid);
                    }
                    if (!string.IsNullOrEmpty(reg.ChosenLoadoutSet))
                    {
                        foreach (var uid in reg.PlayersGuids)
                        {
                            PlayerLoadoutChoices[uid] = reg.ChosenLoadoutSet;
                        }
                    }
                    if (reg.PerPlayerLoadoutChoices != null)
                    {
                        foreach (var kv in reg.PerPlayerLoadoutChoices)
                        {
                            if (!string.IsNullOrEmpty(kv.Value))
                            {
                                PlayerLoadoutChoices[kv.Key] = kv.Value;
                                ExplicitLoadoutChoiceUids.Add(kv.Key);
                            }
                        }
                    }
                }
            }
            this.Id = Interlocked.Increment(ref lastId);
            PersistIdCounter(api);
        }

        public void EliminatePlayer(string playerUid)
        {
            RemoveFromPlay(playerUid);
            CheckLastGroupStanding();
        }

        // No last-team-standing check, for respawn modes like Juggernaut.
        public void RemoveFromPlay(string playerUid) => Roster.RemoveFromPlay(playerUid);

        public bool Admit(string uid, int teamIdx)
        {
            if (!Roster.Admit(uid, teamIdx)) return false;
            ClearReleased(uid);
            leftInPlayUids.Remove(uid);
            return true;
        }

        // Keeps PlayerLoadoutChoices: a rejoin's class offer starts on the class they had.
        public void Withdraw(string uid) => Roster.Withdraw(uid);

        public void ReturnToPlay(string uid) => Roster.ReturnToPlay(uid);

        public int CountInTeam(int teamIdx) => Roster.CountInTeam(teamIdx);

        public void CheckLastGroupStanding()
        {
            // An open match runs on with one team, or none.
            if (IsOpenMatch) return;
            if (ParticipantsByGroupsStillInEvent.Count > 1) return;

            RequestFinish(ParticipantsByGroupsStillInEvent.Count == 1
                ? "one group left standing"
                : "no groups left");
        }

        // null/empty = draw. Overrides the last-group fallback.
        public void SetWinners(IEnumerable<string> winners) => Roster.SetWinners(winners);

        public HashSet<string> GetWinners() => Roster.GetWinners();
    }
}
