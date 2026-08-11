using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using canevents.src.EventManagers.events;
using canevents.src.EventPlacesManagers;
using canevents.src.EventPlacesManagers.Places;
using canevents.src.Network;
using canparty;
using canevents.src.EventManagers.eventBehavior;
using canevents.src.QueueManagers;
using canevents.src.Scoring;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.API.Util;

namespace canevents.src.EventManagers
{
    /// <summary>
    /// Where a live match came from. Observers of <see cref="CANEventManager.OnMatchStarted"/> that
    /// care about the queue must be able to tell a queue-driven start from one dictated externally:
    /// a tournament match of the same event code has nothing to do with whatever the queue was doing.
    /// </summary>
    public enum MatchStartSource
    {
        /// <summary>The queue manager assembled it from waiting registrations.</summary>
        Queue,
        /// <summary>An admin started it from whoever was queued, ignoring the mode's thresholds.</summary>
        ForceStart,
        /// <summary>The line-up was dictated by something else - the tournament driver, a debug match.</summary>
        External
    }

    public partial class CANEventManager
    {
        public HashSet<CANEvent> Events { get; }

        /// <summary>The event with this code, or null. Case-insensitive: codes arrive from chat,
        /// panel fields and config files in varying case.</summary>
        public CANEvent FindEventByCode(string code)
        {
            if (string.IsNullOrEmpty(code)) return null;
            return Events.FirstOrDefault(e => e.Code != null
                && e.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
        }
        public Dictionary<string, QueueManager> QueueManagers { get; set; }
        PartyManager PartyManager { get; set; }
        private Action<canparty.PlayerParty> _onPartyChanged;
        private Action<canparty.PlayerParty, string> _onPartyDisbanded;

        // Registration store: single-lock, replaces the old ConcurrentDictionary pair.
        internal RegistrationStore _store = new RegistrationStore();

        public DefaultEventPlacesManager eventPlacesManager;
        /// <summary>Every per-behavior place edit; both chat commands and admin panel packets go through it.</summary>
        public PlaceConfigService placeConfig;
        public List<RunningEvent> CurrentlyRunningEvents = new();
        ICoreServerAPI sapi;

        /// <summary>Everything the match driver writes to the log, under one tag.</summary>
        internal Log log;

        Config config;
        // Matchmaking tick listeners. RegisterGameTickListener rather than Event.Timer (which returns
        // void → unstoppable), so a mod reload can't leak them firing onto a dead manager.
        private long composeTeamsListenerId = -1;
        private long findLocationListenerId = -1;
        // Delegated from MatchServices: single source of truth, no dual assignment.
        public ServerNetworkHandler NetworkHandler  => MatchServices?.NetworkHandler;
        public LeaderboardData      Leaderboard     => MatchServices?.Leaderboard;
        public SpectatorManager     SpectatorManager => MatchServices?.Spectator;
        public RewardDistributor RewardDistributor { get; set; }
        public Scoring.MatchHistory MatchHistory { get; set; }
        public List<ArenaTemplate> ArenaTemplates { get; set; } = new List<ArenaTemplate>();
        public SchematicPlacer SchematicPlacer { get; set; }
        public TemplateEditor TemplateEditor { get; set; }
        public LoadoutManager LoadoutManager { get; set; }
        public LoadoutPreferences LoadoutPreferences { get; set; }

        /// <summary>Services bundle passed to every RunningEvent at creation. Set by canevents.cs
        /// before calling Start().</summary>
        public MatchServices MatchServices { get; set; }

        /// <summary>Rewards, standings, result screen and history for a finished match.</summary>
        private MatchResultRecorder resultRecorder;

        /// <summary>In-place editor for the event JSON files.</summary>
        private readonly EventConfigStore eventConfigStore = new EventConfigStore();

        /// <summary>Join/leave plus the follow-ups every sign-up needs (party loadout prompts,
        /// browser refresh). Both chat commands and GUI packets go through it.</summary>
        public QueueService Queueing { get; private set; }

        /// <summary>Admin toggles that outlive a restart: disabled events and the queue blacklist.</summary>
        public AdminStateStore AdminState { get; private set; }

        // Pass-throughs: callers read these sets in many places; the store is the only writer.
        public HashSet<string> DisabledEventCodes => AdminState.DisabledEventCodes;
        public HashSet<string> BlacklistedUids => AdminState.BlacklistedUids;

        /// <summary>
        /// Wires the busy-player predicate and starts the periodic timers; call from canevents.cs after
        /// MatchServices is assigned. Not done in the constructor so timers never fire with a null
        /// MatchServices or an unwired busy-player check.
        /// </summary>
        public void Start()
        {
            foreach (var qm in QueueManagers.Values.Distinct())
            {
                qm.SetPlayerBusyCheck(IsPlayerBusy);
                qm.SetRatingProvider((uid, eventCode) =>
                    Leaderboard?.GetRating(uid, eventCode) ?? Scoring.EloCalculator.DefaultRating);
            }

            composeTeamsListenerId = sapi.Event.RegisterGameTickListener(
                dt => AttemptToComposeTeams(), config.attemptComposeTeamsEveryNSeconds * 1000);
            findLocationListenerId = sapi.Event.RegisterGameTickListener(
                dt => AttemptFindLocationForEvent(), config.attemptFindLocationForRunningEventEveryNSeconds * 1000);
        }

        /// <summary>
        /// Raised once a match finished and its result was recorded. Fires from the finishing match's
        /// own tick inside FinishEvent, before it leaves CurrentlyRunningEvents: observers must be
        /// read-only and must NOT start another match from here.
        /// </summary>
        /// <summary>Raised the moment a match becomes live, whichever path assembled it: the queue,
        /// the admin force-start or a tournament. Observers that must react to "this event is now
        /// running" hook here instead of being called by hand from each start path; the source tells
        /// them which of those it was.</summary>
        public event Action<RunningEvent, MatchStartSource> OnMatchStarted;

        public event Action<RunningEvent, MatchResult> OnMatchFinished;

        /// <summary>Raised when an assembled match was dropped before it ever ran (no arena in time).</summary>
        public event Action<RunningEvent, string> OnMatchAborted;

        /// <summary>The tournament driver, when one is wired. Null on servers that never run one.</summary>
        public Tournaments.TournamentManager Tournaments { get; set; }

        /// <summary>
        /// Asked before assembling a match: true means "leave this event's queue alone for now".
        /// A delegate rather than a reference to the feature that uses it (signup windows), so the
        /// match driver keeps knowing nothing about it. Null = nothing holds anything.
        /// </summary>
        // Namespace spelled out: Vintagestory.API.Common declares a Func<> of its own and both
        // namespaces are in scope here (same reason as DropRegistrations in the Queue partial).
        public System.Func<string, bool> QueueHold { get; set; }

        /// <summary>This player's party roster, or null when they are not in one. Lets a tournament
        /// signup take the same snapshot the queue does, without touching the queue.</summary>
        public List<string> PartyRosterOf(IServerPlayer player, out bool isLeader)
        {
            isLeader = false;
            if (player == null || PartyManager == null) return null;
            if (!PartyManager.GetPlayerParty(player, out var party)) return null;
            isLeader = party.leaderGuid == player.PlayerUID;
            return party.playersGuids.ToList();
        }

        /// <summary>Public face of the busy-player check, for drivers outside the queue (tournaments).</summary>
        public bool IsBusy(string uid) => IsPlayerBusy(uid);

        /// <summary>
        /// Busy for the queue and the scheduler: in a match, spectating, or called for a tournament
        /// match that is about to start.
        /// </summary>
        private bool IsPlayerBusy(string uid)
        {
            // A called tournament match makes the player off-limits for the ready window.
            if (Tournaments != null && Tournaments.IsCalled(uid)) return true;
            return IsOccupiedByAMatch(uid);
        }

        /// <summary>
        /// Busy in the only sense that blocks STARTING a match: already playing one, or spectating.
        /// Deliberately does not count "called for a tournament match" - those players are exactly
        /// the ones the tournament is about to hand over, and counting them made the driver refuse
        /// to start its own match with "player is already in a match".
        /// </summary>
        private bool IsOccupiedByAMatch(string uid)
        {
            if (SpectatorManager != null && SpectatorManager.IsSpectating(uid)) return true;
            lock (CurrentlyRunningEvents)
            {
                foreach (var re in CurrentlyRunningEvents)
                    if (re.EventState != EventState.Finished && re.IsParticipant(uid))
                        return true;
            }
            return false;
        }

        /// <summary>
        /// A copy of the live match list, taken under the same lock every mutation uses. For callers
        /// outside this class that iterate it - a foreach over the live list races with a match
        /// starting or ending and throws mid-iteration.
        /// </summary>
        public List<RunningEvent> RunningEventsSnapshot()
        {
            lock (CurrentlyRunningEvents)
                return new List<RunningEvent>(CurrentlyRunningEvents);
        }

        /// <summary>
        /// Runs one lifecycle hook on every behavior of a match, isolating each: a throwing behavior
        /// must not skip the ones after it (a failed inventory restore would strand players in the
        /// arena). <paramref name="hook"/> is used for the log line only.
        /// </summary>
        internal void DispatchToBehaviors(RunningEvent re, string hook, Action<CANEventBehavior> call)
        {
            if (re?.Behaviors == null) return;

            foreach (var beh in re.Behaviors)
            {
                var b = beh; // captured by the lambda below
                log.Safely(b.Name + "." + hook + " for " + re.Event?.Code, () => call(b));
            }
        }

        /// <summary>The live match with this id, or null. Match ids travel to clients and back, so
        /// this is the lookup on the way in.</summary>
        public RunningEvent FindRunningEvent(long id)
        {
            lock (CurrentlyRunningEvents)
            {
                foreach (var re in CurrentlyRunningEvents)
                    if (re.Id == id) return re;
            }
            return null;
        }

        /// <summary>Is this player on the roster of a match that is still going? Crash-recovery paths
        /// ask this first: undoing anything is only safe once no live match owns them.</summary>
        internal bool IsInLiveEvent(string uid)
        {
            lock (CurrentlyRunningEvents)
            {
                foreach (var re in CurrentlyRunningEvents)
                    if (re.IsParticipant(uid)) return true;
            }
            return false;
        }

        /// <summary>Is this player still in play (on the roster and not eliminated)? Stricter than
        /// <see cref="IsInLiveEvent"/>: an eliminated player's arena state is no longer needed.</summary>
        internal bool IsPlayingLiveEvent(string uid)
        {
            lock (CurrentlyRunningEvents)
            {
                foreach (var re in CurrentlyRunningEvents)
                    if (re.IsActive(uid)) return true;
            }
            return false;
        }

        public bool IsEventEnabled(string code) => AdminState.IsEventEnabled(code);

        public bool IsPlayerBlacklisted(string uid) => AdminState.IsPlayerBlacklisted(uid);

        public CANEventManager(PartyManager partyManager, ICoreServerAPI sapi, HashSet<CANEvent> events)
        {
            Events = events;
            PartyManager = partyManager;
            // A party registration is a snapshot taken at /canreg time; once the party shape changes
            // it is stale, so drop it whole - otherwise ex-members get teleported into events and the
            // rest are stranded in a queue they can't leave.
            _onPartyChanged   = _ => DropStalePartyRegistrations();
            _onPartyDisbanded = (_, __) => DropStalePartyRegistrations();
            partyManager.OnPartyChanged   += _onPartyChanged;
            partyManager.OnPartyDisbanded += _onPartyDisbanded;
            this.sapi = sapi;
            log = Log.For(sapi?.World?.Logger, "match");
            this.config = sapi.ModLoader.GetModSystem<canevents>().config;
            RunningEvent.SeedIdCounter(sapi);
            this.QueueManagers = new Dictionary<string, QueueManager>();
            string folderPath = Path.Combine(GamePaths.ModConfig, "canevents");
            if (!Path.Exists(folderPath))
            {
                Directory.CreateDirectory(folderPath);
            }
            AdminState = new AdminStateStore(sapi, config);
            Queueing = new QueueService(this);
            resultRecorder = new MatchResultRecorder(this, sapi, config);
            eventPlacesManager = new DefaultEventPlacesManager(Path.Combine(folderPath, config.eventPlaceFolderName), sapi.Logger);
            eventPlacesManager.LoadEventPlaces();
            placeConfig = new PlaceConfigService(eventPlacesManager);

            LoadoutManager = new LoadoutManager(folderPath, sapi.Logger);
            LoadoutManager.Load(events.Select(e => e.Code));
            LoadoutPreferences = new LoadoutPreferences(sapi);

            // The busy-player predicate is wired after services are ready (see Start()).
            var fullPartyQM     = new FullPartyRequiredQueueManager();
            var byPartsQM       = new ByPartsPartyCollectedQueueManager();
            var rankedQM        = new RankedQueueManager(config.rankedMmrBaseTolerance, config.rankedMmrToleranceWidenPerSecond);
            var rankedByPartsQM = new RankedByPartsQueueManager(config.rankedMmrBaseTolerance, config.rankedMmrToleranceWidenPerSecond);

            // Rated is checked first within each assembly style: a rated event must never fall into
            // a manager that throws teams together at random, because its result feeds Elo.
            foreach (var ev in events)
            {
                if (ev.AssemblingEB is ByPartsPartyCollected)
                    QueueManagers[ev.Code] = ev.Rated ? (QueueManager)rankedByPartsQM : byPartsQM;
                else if (ev.Rated)
                    QueueManagers[ev.Code] = rankedQM;      // skill-based matchmaking on the rated track
                else
                    QueueManagers[ev.Code] = fullPartyQM;   // FullPartyRequired + fallback (FIFO)
            }

            sapi.Event.RegisterEventBusListener(StartEvent, 0.5, "eventLocationFound");
        }

        public void Dispose()
        {
            if (PartyManager != null)
            {
                PartyManager.OnPartyChanged   -= _onPartyChanged;
                PartyManager.OnPartyDisbanded -= _onPartyDisbanded;
            }
            if (composeTeamsListenerId >= 0) sapi?.Event.UnregisterGameTickListener(composeTeamsListenerId);
            if (findLocationListenerId >= 0) sapi?.Event.UnregisterGameTickListener(findLocationListenerId);
            sapi?.Event.UnregisterEventBusListener(StartEvent);
        }

        // camelCase + drop-nulls so written JSON matches the hand-authored event files (which use
        // "winnerItems", "rakePercent", ...) and never persists an inherited (null) field.
        private static readonly JsonSerializer EventConfigSerializer = JsonSerializer.Create(new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Ignore
        });

        /// <summary>
        /// Applies an admin edit of an event's reward + entry-fee: updates the live <see cref="CANEvent"/>
        /// (effective without a restart) and rewrites the event's JSON in ModConfig (survives one). Only
        /// the "reward"/"entryFee" keys are touched. Returns (ok, message) for the admin toast.
        /// </summary>
        public (bool ok, string message) UpdateEventRewardConfig(string code, RewardSpec reward, EntryFeeSpec fee)
        {
            if (string.IsNullOrEmpty(code)) return (false, Lang.Get("canevents:rewardcfg-no-code"));
            var ev = FindEventByCode(code);
            if (ev == null) return (false, Lang.Get("canevents:event-not-found", code));

            ev.Reward = reward;
            ev.EntryFee = fee;

            if (!PersistEventRewardConfig(ev.Code, reward, fee, out string err))
                return (true, Lang.Get("canevents:rewardcfg-save-failed", err));
            return (true, Lang.Get("canevents:rewardcfg-saved", ev.Code));
        }

        /// <summary>
        /// Applies an admin edit of the global reward / anti-farm limits: mutates the shared
        /// <see cref="config"/> (read per match, so most fields take effect at once), rebuilds the
        /// rate-cap limiter, and persists the mod config.
        /// </summary>
        public void ApplyRewardLimitsConfig(int rateCapBurst, double rateCapRefillHours, bool rateCapPerEvent,
            int minMatchSeconds, bool refundForfeitOnVoid, bool participation,
            string winnerItemCode, int winnerQuantity, string participationItemCode, int participationQuantity)
        {
            config.rewardRateCapBurst = Math.Max(0, rateCapBurst);
            config.rewardRateCapRefillHours = Math.Max(0.01, rateCapRefillHours);
            config.rewardRateCapPerEvent = rateCapPerEvent;
            config.minMatchDurationSecondsForReward = Math.Max(0, minMatchSeconds);
            config.entryFeeRefundForfeitOnVoid = refundForfeitOnVoid;
            config.rewardParticipation = participation;
            config.rewardWinnerItemCode = winnerItemCode ?? "";
            config.rewardWinnerQuantity = Math.Max(0, winnerQuantity);
            config.rewardParticipationItemCode = participationItemCode ?? "";
            config.rewardParticipationQuantity = Math.Max(0, participationQuantity);

            RewardDistributor?.ReconfigureRateCap();
            AdminState.Save();
        }

        // Rewrites the "reward"/"entryFee" keys of the matching event JSON, leaving every other key
        // intact. A null spec removes its key (clears the override).
        private bool PersistEventRewardConfig(string code, RewardSpec reward, EntryFeeSpec fee, out string error)
            => eventConfigStore.Edit(code, obj =>
            {
                SetOrRemove(obj, "reward", reward);
                SetOrRemove(obj, "entryFee", fee);
                return null;
            }, out error);

        // Serializes the spec into obj[key], or removes the key when the spec is null or serializes to
        // an empty object (everything inherited → no override to persist).
        private static void SetOrRemove(JObject obj, string key, object spec)
        {
            if (spec == null) { obj.Remove(key); return; }
            var token = JToken.FromObject(spec, EventConfigSerializer);
            if (token is JObject jo && !jo.HasValues) { obj.Remove(key); return; }
            obj[key] = token;
        }

        // Applies an admin edit of a Hunger Games event's loot tables and/or zone phases: mutates the
        // live behavior's Attributes, then persists them into the event JSON. A null part is untouched.
        public (bool ok, string message) UpdateEventHgConfig(string code, JObject lootTables, JArray zonePhases)
        {
            if (string.IsNullOrEmpty(code)) return (false, Lang.Get("canevents:rewardcfg-no-code"));
            var ev = FindEventByCode(code);
            if (ev == null) return (false, Lang.Get("canevents:event-not-found", code));

            var hg = ev.GetBehavior<eventBehavior.HungerGamesEB>();
            if (hg?.Attributes?.Token is JObject attrTok)
            {
                if (lootTables != null) attrTok["lootTables"] = lootTables;
                if (zonePhases != null) attrTok["zonePhases"] = zonePhases;
                // Rewrap so JsonObject re-reads the mutated token rather than any cached child wrappers.
                hg.Attributes = new Vintagestory.API.Datastructures.JsonObject(attrTok);
            }

            if (!PersistEventHgConfig(ev.Code, lootTables, zonePhases, out string err))
                return (true, Lang.Get("canevents:rewardcfg-save-failed", err));
            return (true, Lang.Get("canevents:hgcfg-saved", ev.Code));
        }

        // Writes lootTables/zonePhases into the "HungerGames" behavior's "attributes" of the matching
        // event JSON, leaving every other key intact.
        private bool PersistEventHgConfig(string code, JObject lootTables, JArray zonePhases, out string error)
            => eventConfigStore.Edit(code, obj =>
            {
                if (!(obj["behaviors"] is JArray behs)) return "no behaviors array";

                JObject hgBeh = null;
                foreach (var b in behs)
                    if (b is JObject bo && bo["name"]?.Value<string>() == "HungerGames") { hgBeh = bo; break; }
                if (hgBeh == null) return "no HungerGames behavior";

                if (!(hgBeh["attributes"] is JObject attr)) { attr = new JObject(); hgBeh["attributes"] = attr; }
                if (lootTables != null) attr["lootTables"] = lootTables;
                if (zonePhases != null) attr["zonePhases"] = zonePhases;
                return null;
            }, out error);
    }
}
