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
        Finished
    }
    public class RunningEvent
    {
        // Persisted across server restarts so EventIds don't collide with stale
        // client-side state (e.g. a queued LoadoutPickRequest with an old EventId).
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

        // Persists the current max, not a captured value, so interleaved calls still save the largest
        // issued id.
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

        /// <summary>The bot bodies in this match - the physical side only (spawning, gear, despawn);
        /// the roster itself sees bots and players alike as uids.</summary>
        public ArenaBotManager Bots => bots ??= new ArenaBotManager(this);
        private ArenaBotManager bots;

        /// <summary>Set while the mode forbids fighting (Hunger Games' truce and pedestal freeze).
        /// Players are held to it by damage hooks; bots read this flag and refuse to pick a target.</summary>
        public bool CombatSuspended { get; set; }

        /// <summary>When non-empty, only these EventPlace ids may host this match (a tournament's
        /// arena whitelist). Null/empty keeps the normal "any suitable arena" behavior.</summary>
        public HashSet<long> AllowedPlaceIds { get; set; }

        /// <summary>Stakes are owned outside the match (a tournament pot, paid once at the end), so
        /// EntryFeeSettlement skips it entirely.</summary>
        public bool SuppressEntryFeeSettlement { get; set; }

        /// <summary>Keeps this match out of standings and Elo (an exhibition/unrated tournament).</summary>
        public bool SuppressRating { get; set; }

        /// <summary>Pays no event reward: a tournament sub-match, where the bracket pays its own
        /// prizes once at the end instead of once per round.</summary>
        public bool SuppressRewards { get; set; }

        /// <summary>Chunk columns pinned for this match, released in cleanup: a bots-only match has
        /// nothing else holding its arena in memory.</summary>
        public List<Vintagestory.API.MathTools.Vec2i> PinnedChunks { get; set; }

        /// <summary>Set while participants must stay put (the pedestal freeze). Separate from
        /// <see cref="CombatSuspended"/>: during the truce bots may walk, they just may not fight.</summary>
        public bool MovementFrozen { get; set; }

        /// <summary>The body playing under this uid - a player's entity or a bot's. Modes that act on
        /// the world want this instead of PlayerByUid, which silently skips every bot.</summary>
        public Vintagestory.API.Common.Entities.Entity BodyOf(string uid)
        {
            if (uid == null) return null;
            if (BotIdentity.IsBot(uid)) return Bots.EntityOf(uid);
            return (sapi.World.PlayerByUid(uid) as IServerPlayer)?.Entity;
        }

        /// <summary>This match's behavior of the given type, or null - the same lookup as
        /// <see cref="CANEvent.GetBehavior{T}"/> but over the per-match clones.</summary>
        public T GetBehavior<T>() where T : class
        {
            for (int i = 0; i < Behaviors.Count; i++)
                if (Behaviors[i] is T match) return match;
            return null;
        }

        // The single behavior that scores/ranks this match, or null. Resolved once in the constructor.
        public IMatchScorer Scorer { get; private set; }
        // The match roster lives in MatchRoster; the members below forward to it.
        public MatchRoster Roster { get; } = new MatchRoster();

        public IReadOnlyList<string> ParticipantsBefore => Roster.ParticipantsBefore;
        public IReadOnlyList<HashSet<string>> ParticipantsByGroupsBefore => Roster.ParticipantsByGroupsBefore;
        public IReadOnlyCollection<string> ParticipantsStillInEvent => Roster.ParticipantsStillInEvent;
        public IReadOnlyList<HashSet<string>> ParticipantsByGroupsStillInEvent => Roster.ParticipantsByGroupsStillInEvent;

        /// <summary>Everyone in the starting line-up who is online right now; bots and offline
        /// participants have no player object and are skipped.</summary>
        public IEnumerable<IServerPlayer> OnlineParticipants
        {
            get
            {
                foreach (var uid in ParticipantsBefore)
                    if (sapi.World.PlayerByUid(uid) is IServerPlayer player) yield return player;
            }
        }

        /// <summary>The same, narrowed to those still in play - eliminated players are left out.</summary>
        public IEnumerable<IServerPlayer> OnlineActiveParticipants
        {
            get
            {
                foreach (var uid in ParticipantsStillInEvent)
                    if (sapi.World.PlayerByUid(uid) is IServerPlayer player) yield return player;
            }
        }

        /// <summary>Marks a participant who disconnected before the match started, so
        /// <see cref="InitializeRoster"/> leaves them out. No-op once running - use
        /// <see cref="EliminatePlayer"/> then.</summary>
        public void MarkPreStartDropout(string uid) => Roster.MarkPreStartDropout(uid);

        /// <summary>Copies the starting line-up into the active one, at the moment the match actually starts.</summary>
        public void InitializeRoster() => Roster.Initialize();

        /// <summary>Position of a player in the starting line-up, or -1. Used for spawn assignment.</summary>
        public int IndexInStartingLineup(string uid) => Roster.IndexInStartingLineup(uid);
        /// <summary>Set once the match has a reason to end; the manager picks it up on its next tick.
        /// Write it through <see cref="RequestFinish"/>, which records why.</summary>
        public bool ShouldFinish { get; private set; }

        /// <summary>Asks for the match to end. Idempotent: the first reason wins, so a mode that both
        /// eliminates the last player and declares a winner doesn't overwrite the cause. Logged only.</summary>
        public void RequestFinish(string reason)
        {
            if (ShouldFinish) return;
            ShouldFinish = true;
            sapi?.World?.Logger?.VerboseDebug("[canevents] Event {0} ({1}) finishing: {2}",
                Id, Event?.Code, reason ?? "unspecified");
        }
        /// <summary>Wall-clock limit of the match, compared against <see cref="ElapsedSeconds"/>. Set
        /// via <see cref="SetDuration"/> before the start or <see cref="ShortenDuration"/> during.</summary>
        public float MaxDurationSeconds { get; private set; } = 300f;

        /// <summary>The mode's time limit, applied in OnBeforeStart. Ignores non-positive values.</summary>
        public void SetDuration(float seconds)
        {
            if (seconds > 0f) MaxDurationSeconds = seconds;
        }

        /// <summary>Cuts a running match short (Parkour's grace countdown). Only ever shortens -
        /// extending would move a deadline players are already racing against. True if it changed.</summary>
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
        // Wall-clock seconds spent in SearchingForLocation, accumulated by the find-location tick. Drives
        // the give-up timeout so an assembled match whose arena vanished doesn't hold its players forever.
        public float SearchingSeconds { get; set; } = 0f;
        public EventPlace Place { get; set; }
        public EventState EventState { get; set; }
        public long Id { get; set; } = 0;
        public ICoreServerAPI sapi { get; private set; }

        /// <summary>Services injected at construction, available in every lifecycle hook including
        /// OnBeforeStart. Never null after the standard constructor.</summary>
        public MatchServices Services { get; private set; }

        public Network.ServerNetworkHandler  NetworkHandler => Services?.NetworkHandler;
        public Scoring.LeaderboardData       Leaderboard    => Services?.Leaderboard;
        public LoadoutManager                LoadoutManager => Services?.LoadoutManager;
        public SpectatorManager              Spectator      => Services?.Spectator;
        public Config                        Config         => Services?.Config;
        /// <summary>True when the match runs on the players' own gear. Modes MUST check it before
        /// issuing anything: nothing was cleared and nothing is restored, so an issued kit lands on
        /// top of real belongings and leaves the arena as minted property.</summary>
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

        // Tick-listener ids for this event's lifetime, bulk-unregistered in FinishEvent. A list, not a
        // name-keyed dict, so two behaviors with the same Name can't clobber each other.
        public List<long> RegisteredTickListeners = new List<long>();

        /// <summary>Registers a tick listener that dies with the match: a listener missing from the
        /// list keeps firing into a finished match. Returns the handle for early stopping.</summary>
        public long RegisterMatchTick(Action<float> onTick, int intervalMs)
        {
            long id = sapi.Event.RegisterGameTickListener(dt => onTick(dt), intervalMs);
            RegisteredTickListeners.Add(id);
            return id;
        }
        // Kept separate: needs an explicit unregister mid-life, when the countdown elapses. -1 = not set.
        public long CountdownListenerId = -1;
        /// <summary>Is this player still in play (not eliminated, not finished)?</summary>
        public bool IsActive(string uid) => Roster.IsActive(uid);

        /// <summary>Did this player start the match? Stays true after elimination: this is the set that
        /// gets rewards, inventory restores and end-of-match messages.</summary>
        public bool IsParticipant(string uid) => Roster.IsParticipant(uid);

        /// <summary>The team this player started on, or -1 when they are not a participant. Index into
        /// <see cref="ParticipantsByGroupsBefore"/>, the same numbering spawn points use.</summary>
        public int TeamIndexOf(string uid) => Roster.TeamIndexOf(uid);

        public Dictionary<string, string> PlayerLoadoutChoices = new Dictionary<string, string>();
        /// <summary>Uids with an explicit per-player loadout pick; the match-start popup is skipped for them.</summary>
        public HashSet<string> ExplicitLoadoutChoiceUids = new HashSet<string>();
        // Players who registered the party; their loadout pick was explicit, so no popup for them.
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

            // The registrant's pick applies to all party members as a fallback; per-member overrides
            // come later from saved defaults or match-start popup responses.
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
                    // Per-member picks made at queue-time override the leader's choice.
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

        /// <summary>Takes a player out of play and, if that leaves one group or none, ends the match.
        /// The default for elimination modes (last team standing).</summary>
        public void EliminatePlayer(string playerUid)
        {
            RemoveFromPlay(playerUid);
            CheckLastGroupStanding();
        }

        /// <summary>Takes a player out of play WITHOUT deciding the match. For respawn modes such as
        /// Juggernaut, where "one group left" says nothing about who is winning.</summary>
        public void RemoveFromPlay(string playerUid) => Roster.RemoveFromPlay(playerUid);

        /// <summary>Ends the match when one group (or none) is left standing. Separate from
        /// <see cref="RemoveFromPlay"/> so a mode can decide whether this rule applies.</summary>
        public void CheckLastGroupStanding()
        {
            if (ParticipantsByGroupsStillInEvent.Count > 1) return;

            RequestFinish(ParticipantsByGroupsStillInEvent.Count == 1
                ? "one group left standing"
                : "no groups left");
        }

        /// <summary>Declares the winning UIDs; null/empty means "draw". Takes precedence over the
        /// single-remaining-group fallback in GetWinners().</summary>
        public void SetWinners(IEnumerable<string> winners) => Roster.SetWinners(winners);

        /// <summary>The winning group's UIDs, or empty. Prefers explicit winners, else falls back to
        /// the single remaining group (last team standing).</summary>
        public HashSet<string> GetWinners() => Roster.GetWinners();
    }
}
