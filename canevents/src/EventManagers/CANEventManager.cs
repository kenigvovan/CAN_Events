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
    public enum MatchStartSource
    {
        Queue,
        // Admin start from whoever was queued, ignoring thresholds.
        ForceStart,
        // Tournament or debug match.
        External
    }

    public partial class CANEventManager
    {
        public HashSet<CANEvent> Events { get; }

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

        internal RegistrationStore _store = new RegistrationStore();

        public DefaultEventPlacesManager eventPlacesManager;
        public PlaceConfigService placeConfig;
        // uid -> place id from /canep select. Session only.
        public readonly Dictionary<string, long> SelectedPlaceByPlayer = new Dictionary<string, long>();

        public PlayerState PlayerState { get; private set; }

        public AdminActions AdminActions { get; private set; }

        public eventBehavior.Shared.HungerGamesSetupService HungerGamesSetup { get; private set; }
        // Private: ending a match removes it from this list, so iterate via RunningEventsSnapshot().
        private readonly List<RunningEvent> runningEvents = new();

        public int RunningEventCount
        {
            get { lock (runningEvents) return runningEvents.Count; }
        }

        // For the scheduler's concurrency cap: open matches run forever and would hold a slot for good.
        public int ScheduledMatchCount
        {
            get { lock (runningEvents) return runningEvents.Count(re => !re.IsOpenMatch); }
        }
        ICoreServerAPI sapi;

        internal Log log;

        Config config;
        // Tick listeners, not Event.Timer: a timer can't be stopped and would leak across a mod reload.
        private long composeTeamsListenerId = -1;
        private long findLocationListenerId = -1;
        public ServerNetworkHandler NetworkHandler  => MatchServices?.NetworkHandler;
        public LeaderboardData      Leaderboard     => MatchServices?.Leaderboard;
        public SpectatorManager     SpectatorManager => MatchServices?.Spectator;
        public RewardDistributor RewardDistributor { get; set; }
        public Scoring.MatchHistory MatchHistory { get; set; }
        public Scoring.HeadToHeadStore HeadToHead { get; set; }
        public List<ArenaTemplate> ArenaTemplates { get; set; } = new List<ArenaTemplate>();
        public SchematicPlacer SchematicPlacer { get; set; }
        public TemplateEditor TemplateEditor { get; set; }
        public IReadOnlyDictionary<string, LoadoutManager> ItemSetStores { get; private set; }
            = new Dictionary<string, LoadoutManager>();

        // null for an unknown kind; don't fall back to loadouts for kinds from the wire.
        public LoadoutManager Store(string kind)
            => ItemSetStores.TryGetValue(ItemSetKinds.Normalize(kind), out var s) ? s : null;

        public LoadoutManager LoadoutManager => Store(ItemSetKinds.Loadout);

        public LoadoutPreferences LoadoutPreferences { get; set; }

        // Events may override it.
        public eventBehavior.Shared.IssuedGearDrop IssuedGearDefault { get; private set; }
            = eventBehavior.Shared.IssuedGear.DefaultPolicy;

        // Set by canevents.cs before Start().
        public MatchServices MatchServices { get; set; }

        private MatchResultRecorder resultRecorder;

        private readonly EventConfigStore eventConfigStore = new EventConfigStore();

        public QueueService Queueing { get; private set; }

        public AdminStateStore AdminState { get; private set; }

        public HashSet<string> DisabledEventCodes => AdminState.DisabledEventCodes;
        public HashSet<string> BlacklistedUids => AdminState.BlacklistedUids;

        // Called after MatchServices is assigned, so timers never fire before it's wired.
        public void Start()
        {
            // Parsed once so a typo doesn't log on every dropped item.
            IssuedGearDefault = eventBehavior.Shared.IssuedGear.ParseDefault(config.issuedGearDrop, log);

            // All four: a definition reload can move an event to a style nothing used at startup.
            foreach (var qm in new[] { fullPartyQM, byPartsQM, rankedQM, rankedByPartsQM })
            {
                qm.SetPlayerBusyCheck(IsPlayerBusy);
                qm.SetRatingProvider((uid, eventCode) =>
                    Leaderboard?.GetRating(uid, eventCode) ?? Scoring.EloCalculator.DefaultRating);
            }

            ApplyTickIntervals();
            StartOpenMatchKeeper();
        }

        // Re-run after a config change: CountSearchTimeout assumes the configured interval is the real one.
        public void ApplyTickIntervals()
        {
            if (composeTeamsListenerId >= 0) sapi.Event.UnregisterGameTickListener(composeTeamsListenerId);
            if (findLocationListenerId >= 0) sapi.Event.UnregisterGameTickListener(findLocationListenerId);

            // At least a second: 0 would run the pass on every server tick.
            composeTeamsListenerId = sapi.Event.RegisterGameTickListener(
                dt => AttemptToComposeTeams(), Math.Max(1, config.attemptComposeTeamsEveryNSeconds) * 1000);
            findLocationListenerId = sapi.Event.RegisterGameTickListener(dt =>
            {
                // Ready checks first so a confirmed match finds an arena in the same pass.
                AttemptReadyChecks();
                AttemptFindLocationForEvent();
            }, Math.Max(1, config.attemptFindLocationForRunningEventEveryNSeconds) * 1000);
        }

        // With a ready check, fires only after it passes.
        public event Action<RunningEvent, MatchStartSource> OnMatchStarted;

        // Fires inside FinishEvent before the match leaves the live list; don't start matches from here.
        public event Action<RunningEvent, MatchResult> OnMatchFinished;

        // Assembled but never ran (no arena in time).
        public event Action<RunningEvent, string> OnMatchAborted;

        public Tournaments.TournamentManager Tournaments { get; set; }

        // true = don't assemble this event's queue now (signup windows).
        // System.Func: Vintagestory.API.Common has its own Func<>.
        public System.Func<string, bool> QueueHold { get; set; }

        public List<string> PartyRosterOf(IServerPlayer player, out bool isLeader)
        {
            isLeader = false;
            if (player == null || PartyManager == null) return null;
            if (!PartyManager.GetPlayerParty(player, out var party)) return null;
            isLeader = party.leaderGuid == player.PlayerUID;
            return party.playersGuids.ToList();
        }

        public bool IsBusy(string uid) => IsPlayerBusy(uid);

        private bool IsPlayerBusy(string uid)
        {
            if (Tournaments != null && Tournaments.IsCalled(uid)) return true;
            return IsOccupiedByAMatch(uid);
        }

        // Ignores tournament calls, otherwise the tournament couldn't start its own match.
        // Released and pre-start dropouts are free (R24d): the match no longer holds them.
        internal bool IsOccupiedByAMatch(string uid)
        {
            if (SpectatorManager != null && SpectatorManager.IsSpectating(uid)) return true;
            lock (runningEvents)
            {
                foreach (var re in runningEvents)
                    if (re.EventState != EventState.Finished && re.HoldsPlayer(uid))
                        return true;
            }
            return false;
        }

        public List<RunningEvent> RunningEventsSnapshot()
        {
            lock (runningEvents)
                return new List<RunningEvent>(runningEvents);
        }

        // Running matches only. Safe to end a match inside the loop.
        public IEnumerable<(RunningEvent match, T behavior)> LiveBehaviors<T>() where T : class
        {
            foreach (var re in RunningEventsSnapshot())
            {
                if (re.EventState != EventState.Running) continue;
                var behaviors = re.Behaviors;
                for (int i = 0; i < behaviors.Count; i++)
                    if (behaviors[i] is T found) yield return (re, found);
            }
        }

        private void AddRunningEvent(RunningEvent re)
        {
            lock (runningEvents) runningEvents.Add(re);
        }

        private void RemoveRunningEvent(RunningEvent re)
        {
            lock (runningEvents)
            {
                runningEvents.Remove(re);
                queueRegistrationsByMatch.Remove(re.Id);
            }
        }

        // Match id -> source registrations, queue matches only. Used by the ready check requeue. Guarded by runningEvents lock.
        private readonly Dictionary<long, List<Registration>> queueRegistrationsByMatch = new();

        private bool IsQueueMatch(RunningEvent re)
        {
            lock (runningEvents) return queueRegistrationsByMatch.ContainsKey(re.Id);
        }

        public bool HasPendingReadyCheck(string eventCode)
        {
            lock (runningEvents)
            {
                foreach (var re in runningEvents)
                    if (re.EventState == EventState.AwaitingReady && re.Event.CodeEquals(eventCode))
                        return true;
            }
            return false;
        }

        private void RaiseMatchStarted(RunningEvent re, MatchStartSource source)
            => log.Safely("OnMatchStarted observer for " + re.Event?.Code,
                () => OnMatchStarted?.Invoke(re, source));

        // Each behavior isolated so one throwing doesn't skip the rest (e.g. inventory restore).
        internal void DispatchToBehaviors(RunningEvent re, string hook, Action<CANEventBehavior> call)
        {
            if (re?.Behaviors == null) return;

            foreach (var beh in re.Behaviors)
            {
                var b = beh;
                log.Safely(b.Name + "." + hook + " for " + re.Event?.Code, () => call(b));
            }
        }

        public RunningEvent FindRunningEvent(long id)
        {
            lock (runningEvents)
            {
                foreach (var re in runningEvents)
                    if (re.Id == id) return re;
            }
            return null;
        }

        // Ignores state: gear dropped while finishing still follows that match's rules. A released player
        // has their own gear back and may be in another match by now, so that match's rules are not theirs.
        public RunningEvent FindLiveEventOf(string uid)
        {
            if (uid == null) return null;
            lock (runningEvents)
            {
                foreach (var re in runningEvents)
                    if (re.HoldsPlayer(uid)) return re;
            }
            return null;
        }

        // An open match never reaches the end-of-match sweep, so its kit can't be left lying around.
        public eventBehavior.Shared.IssuedGearDrop PolicyOf(RunningEvent re)
            => re?.IsOpenMatch == true
                ? eventBehavior.Shared.IssuedGearDrop.Remove
                : re?.GetBehavior<eventBehavior.SavePlayerAttributes>()?.IssuedGearDropOverride ?? IssuedGearDefault;

        internal bool IsInLiveEvent(string uid) => FindLiveEventOf(uid) != null;

        // On the roster and not eliminated.
        internal bool IsPlayingLiveEvent(string uid)
        {
            lock (runningEvents)
            {
                foreach (var re in runningEvents)
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
            // Party registration is a snapshot from /canreg; drop it whole once the party changes.
            _onPartyChanged   = _ => DropStalePartyRegistrations();
            _onPartyDisbanded = (_, __) => DropStalePartyRegistrations();
            partyManager.OnPartyChanged   += _onPartyChanged;
            partyManager.OnPartyDisbanded += _onPartyDisbanded;
            this.sapi = sapi;
            log = Log.For(sapi?.World?.Logger, "match");
            this.config = sapi.ModLoader.GetModSystem<canevents>().config;
            RunningEvent.SeedIdCounter(sapi);
            this.QueueManagers = new Dictionary<string, QueueManager>();
            string folderPath = ModPaths.Root;
            if (!Path.Exists(folderPath))
            {
                Directory.CreateDirectory(folderPath);
            }
            AdminState = new AdminStateStore(sapi, config);
            Queueing = new QueueService(this);
            resultRecorder = new MatchResultRecorder(this, sapi, config);
            eventPlacesManager = new DefaultEventPlacesManager(Path.Combine(folderPath, config.eventPlaceFolderName), sapi.Logger);
            eventPlacesManager.LoadEventPlaces();
            // Lazy: SchematicPlacer is assigned after the constructor.
            placeConfig = new PlaceConfigService(eventPlacesManager, () => SchematicPlacer);
            PlayerState = new PlayerState(sapi, this);
            AdminActions = new AdminActions(sapi, this);
            HungerGamesSetup = new eventBehavior.Shared.HungerGamesSetupService(sapi, this);

            var eventCodes = events.Select(e => e.Code).ToList();
            var stores = new Dictionary<string, LoadoutManager>();
            foreach (var kvp in ItemSetKinds.Folders)
            {
                var store = new LoadoutManager(folderPath, sapi.Logger, kvp.Value);
                store.Load(eventCodes);
                stores[kvp.Key] = store;
            }
            ItemSetStores = stores;
            LoadoutPreferences = new LoadoutPreferences(sapi);

            fullPartyQM     = new FullPartyRequiredQueueManager();
            byPartsQM       = new ByPartsPartyCollectedQueueManager();
            rankedQM        = new RankedQueueManager(config.rankedMmrBaseTolerance, config.rankedMmrToleranceWidenPerSecond);
            rankedByPartsQM = new RankedByPartsQueueManager(config.rankedMmrBaseTolerance, config.rankedMmrToleranceWidenPerSecond);

            foreach (var ev in events) AssignQueueManager(ev);

            sapi.Event.RegisterEventBusListener(StartEvent, 0.5, "eventLocationFound");
        }

        // One shared instance per assembly style; fields so a definition reload reuses them.
        private readonly QueueManager fullPartyQM;
        private readonly QueueManager byPartsQM;
        private readonly QueueManager rankedQM;
        private readonly QueueManager rankedByPartsQM;

        private void AssignQueueManager(CANEvent ev)
        {
            if (ev.AssemblingEB is ByPartsPartyCollected)
                QueueManagers[ev.Code] = ev.Rated ? rankedByPartsQM : byPartsQM;
            else if (ev.Rated)
                QueueManagers[ev.Code] = rankedQM;      // skill-based matchmaking on the rated track
            else
                QueueManagers[ev.Code] = fullPartyQM;   // FullPartyRequired + fallback (FIFO)
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
            if (openMatchKeeperId >= 0) sapi?.Event.UnregisterGameTickListener(openMatchKeeperId);
            sapi?.Event.UnregisterEventBusListener(StartEvent);
            HungerGamesSetup?.StopAllPreviews();
        }

        // camelCase, nulls dropped: matches hand-written event files, inherited fields aren't persisted.
        private static readonly JsonSerializer EventConfigSerializer = JsonSerializer.Create(new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Ignore
        });

        // Updates the live event and its JSON in ModConfig.
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

        // A null spec removes the key.
        private bool PersistEventRewardConfig(string code, RewardSpec reward, EntryFeeSpec fee, out string error)
            => eventConfigStore.Edit(code, obj =>
            {
                SetOrRemove(obj, "reward", reward);
                SetOrRemove(obj, "entryFee", fee);
                return null;
            }, out error);

        // Empty object means everything inherited, so the key is removed.
        private static void SetOrRemove(JObject obj, string key, object spec)
        {
            if (spec == null) { obj.Remove(key); return; }
            var token = JToken.FromObject(spec, EventConfigSerializer);
            if (token is JObject jo && !jo.HasValues) { obj.Remove(key); return; }
            obj[key] = token;
        }

        // Edits a copy, so a running match keeps its attributes.
        public (bool ok, string message) EditEventBehaviorAttributes(string code, string behaviorName,
            Action<JObject> edit, string savedLangKey)
        {
            if (string.IsNullOrEmpty(code)) return (false, Lang.Get("canevents:rewardcfg-no-code"));
            var ev = FindEventByCode(code);
            if (ev == null) return (false, Lang.Get("canevents:event-not-found", code));

            var beh = ev.Behaviors.FirstOrDefault(b =>
                string.Equals(b.Name, behaviorName, StringComparison.OrdinalIgnoreCase));
            if (beh == null) return (false, Lang.Get("canevents:rewardcfg-save-failed", "no " + behaviorName + " behavior"));

            var attrs = beh.Attributes?.Token is JObject tok ? (JObject)tok.DeepClone() : new JObject();
            edit(attrs);
            beh.Attributes = new Vintagestory.API.Datastructures.JsonObject(attrs);
            // As on load: rebuilds the template's known settings, which the new Attributes dropped.
            beh.Initialize();

            if (!eventConfigStore.EditBehaviorAttributes(ev.Code, behaviorName, edit, out string err))
                return (true, Lang.Get("canevents:rewardcfg-save-failed", err));
            return (true, Lang.Get(savedLangKey, ev.Code));
        }
    }
}
