using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers;
using canevents.src.EventManagers.eventBehavior.abstracts;
using canevents.src.EventManagers.eventBehavior.Shared;
using canevents.src.EventManagers.events;
using canevents.src.EventPlacesManagers;
using canevents.src.EventPlacesManagers.Places;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace canevents.src.Network
{
    public partial class ClientNetworkHandler
    {
        private ICoreClientAPI capi;

        // Both channels are opened on every client; the server gates the admin one.
        private IClientNetworkChannel clientChannel;
        private IClientNetworkChannel adminChannel;

        // Keyed by EventId.
        private Dictionary<long, EventStateUpdateMsg> EventStates = new Dictionary<long, EventStateUpdateMsg>();
        private Dictionary<long, ScoreboardUpdateMsg> Scoreboards = new Dictionary<long, ScoreboardUpdateMsg>();
        private Dictionary<long, List<KillFeedMsg>> KillFeeds = new Dictionary<long, List<KillFeedMsg>>();
        public long ActiveEventId { get; private set; }

        public EventStateUpdateMsg CurrentEventState =>
            ActiveEventId != 0 && EventStates.TryGetValue(ActiveEventId, out var s) ? s : null;
        public ScoreboardUpdateMsg CurrentScoreboard =>
            ActiveEventId != 0 && Scoreboards.TryGetValue(ActiveEventId, out var s) ? s : null;
        public List<KillFeedMsg> KillFeed =>
            ActiveEventId != 0 && KillFeeds.TryGetValue(ActiveEventId, out var f) ? f : new List<KillFeedMsg>();

        public EventResultMsg LastResult { get; private set; }

        public readonly ServerState<EventListMsg> EventList = new ServerState<EventListMsg>();

        // The list is pushed on change, so timers in it are aged against this.
        private long eventListReceivedMs;

        public double SecondsSinceEventList()
            => eventListReceivedMs == 0 ? 0 : (capi.ElapsedMilliseconds - eventListReceivedMs) / 1000.0;

        // Server allows one queue at a time.
        public EventListEntryMsg QueuedEntry()
        {
            var events = EventList.Value?.Events;
            if (events == null) return null;

            foreach (var e in events)
                if (e.IsQueued && e.EventId == 0) return e;
            return null;
        }

        public readonly ServerState<PlayerAccessListMsg> PlayerAccess = new ServerState<PlayerAccessListMsg>();
        public readonly ServerState<TemplateListMsg> TemplateList = new ServerState<TemplateListMsg>();
        public readonly ServerState<LeaderboardMsg> Leaderboard = new ServerState<LeaderboardMsg>();

        public event Action OnStateUpdated;
        public event Action OnScoreboardUpdated;
        public event Action<KillFeedMsg> OnKillFeedReceived;
        public event Action<EventResultMsg> OnResultReceived;

        // uid -> flag team. Swapped as a whole, never mutated: the renderer reads it every frame.
        public IReadOnlyDictionary<string, int> CtfCarriers { get; private set; }
            = new Dictionary<string, int>();

        // uid -> team for the pennants over heads; swapped whole like CtfCarriers.
        public IReadOnlyDictionary<string, int> TeamMarkers { get; private set; }
            = new Dictionary<string, int>();
        private long teamMarkersEventId;
        public bool TeamMarkersOnHead { get; private set; }

        // A clear from an earlier match must not wipe the pennants of the one now on.
        private void OnTeamMarkers(TeamMarkersMsg msg)
        {
            int n = Math.Min(msg?.Uids?.Length ?? 0, msg?.Teams?.Length ?? 0);
            if (n == 0 && msg != null && msg.EventId != teamMarkersEventId) return;

            var map = new Dictionary<string, int>();
            for (int i = 0; i < n; i++)
                if (!string.IsNullOrEmpty(msg.Uids[i])) map[msg.Uids[i]] = msg.Teams[i];
            teamMarkersEventId = msg?.EventId ?? 0;
            TeamMarkersOnHead = msg?.OnHead ?? false;
            TeamMarkers = map;
        }

        // Received time is used to interpolate the radius.
        public HgZoneStateMsg HgZone { get; private set; }
        public long HgZoneReceivedMs { get; private set; }

        private void OnCtfCarriers(CtfCarriersMsg msg)
        {
            var map = new Dictionary<string, int>();
            if (msg?.CarrierUids != null && msg.FlagTeams != null)
            {
                int n = Math.Min(msg.CarrierUids.Length, msg.FlagTeams.Length);
                for (int i = 0; i < n; i++)
                {
                    if (!string.IsNullOrEmpty(msg.CarrierUids[i])) map[msg.CarrierUids[i]] = msg.FlagTeams[i];
                }
            }
            CtfCarriers = map;
        }

        public ClientNetworkHandler(ICoreClientAPI capi)
        {
            this.capi = capi;

            clientChannel = capi.Network.RegisterChannel(CanEventsProtocol.MainChannelName);
            CanEventsProtocol.Register(clientChannel, ProtocolChannel.Main);
            adminChannel = capi.Network.RegisterChannel(CanEventsProtocol.AdminChannelName);
            CanEventsProtocol.Register(adminChannel, ProtocolChannel.Admin);
            capi.Logger.Notification(CanEventsProtocol.Summary());

            SetHandler<ProtocolHandshakeMsg>(OnServerHandshake);
            SetHandler<ScheduleStateMsg>(ScheduleState.Set);
            SetHandler<ClientConfigSyncMsg>(OnClientConfigSync);
            SetHandler<EventRewardConfigMsg>(EventRewardConfig.Set);
            SetHandler<RewardLimitsMsg>(RewardLimits.Set);
            SetHandler<ServerSettingsMsg>(ServerSettings.Set);
            SetHandler<EventSettingsMsg>(EventSettings.Set);
            SetHandler<AdminEventListMsg>(AdminEventList.Set);
            SetHandler<LoadoutListMsg>(msg => ItemSetList(msg?.Kind).Set(msg));
            SetHandler<PlatformEditStateMsg>(PlatformEditState.Set);
            SetHandler<MatchHistoryMsg>(MatchHistoryData.Set);
            SetHandler<SchematicListMsg>(SchematicList.Set);
            SetHandler<CtfCarriersMsg>(OnCtfCarriers);
            SetHandler<TeamMarkersMsg>(OnTeamMarkers);
            SetHandler<HgZoneStateMsg>(m => { HgZone = m; HgZoneReceivedMs = capi.ElapsedMilliseconds; });
            SetHandler<HgSetupMsg>(HgSetup.Set);
            SetHandler<MmConfigMsg>(MmConfig.Set);
            SetHandler<EventStateUpdateMsg>(OnStateUpdate);
            SetHandler<ScoreboardUpdateMsg>(OnScoreUpdate);
            SetHandler<KillFeedMsg>(OnKillFeed);
            SetHandler<EventResultMsg>(OnResult);
            SetHandler<EventListMsg>(msg =>
            {
                eventListReceivedMs = capi.ElapsedMilliseconds;
                EventList.Set(msg);
            });
            SetHandler<PlaceListMsg>(OnPlaceList);
            SetHandler<PadListMsg>(PadList.Set);
            SetHandler<PlaceOperationResultMsg>(OnPlaceResult);
            SetHandler<TemplateListMsg>(TemplateList.Set);
            SetHandler<LeaderboardMsg>(Leaderboard.Set);
            SetHandler<TplSessionStateMsg>(TplSession.Set);
            SetHandler<PartyStateMsg>(PartyState.Set);
            SetHandler<PartyOperationResultMsg>(OnPartyResult);
            SetHandler<LoadoutPickRequestMsg>(OnLoadoutPickRequest);
            SetHandler<LoadoutDefaultsListMsg>(LoadoutDefaults.Set);
            SetHandler<TournamentListMsg>(OnTournamentList);
            SetHandler<TournamentBracketMsg>(TournamentBracket.Set);
            SetHandler<TournamentReadyRequestMsg>(OnTournamentReadyRequest);
            SetHandler<TournamentReadyClosedMsg>(HandleTournamentReadyClosed);
            SetHandler<EventInviteMsg>(OnEventInvite);
            SetHandler<EventInviteClosedMsg>(HandleEventInviteClosed);
            SetHandler<PlayerAccessListMsg>(PlayerAccess.Set);
            RegisterSpectateHandlers();
            RegisterDuelHandlers();
            RegisterReadyHandlers();
            RegisterTeamPickHandlers();
            RegisterShopHandlers();
            RegisterInventoryAuditHandlers();
        }

        private IClientNetworkChannel ChannelOf(ProtocolChannel channel)
            => channel == ProtocolChannel.Admin ? adminChannel : clientChannel;

        // Sets the handler on every channel the type is on. The engine doesn't catch, so we do.
        private void SetHandler<T>(NetworkServerMessageHandler<T> handler)
        {
            foreach (var channel in CanEventsProtocol.ChannelsOf(typeof(T)))
            {
                ChannelOf(channel).SetMessageHandler<T>(msg =>
                {
                    try
                    {
                        handler(msg);
                    }
                    catch (Exception e)
                    {
                        LogHandlerFailure(typeof(T), e);
                    }
                });
            }
        }

        private void Send<T>(T msg) => ChannelOf(CanEventsProtocol.ChannelFor<T>()).SendPacket(msg);

        // Server ignores this client while versions differ.
        public bool ProtocolMismatch { get; private set; }

        private void OnServerHandshake(ProtocolHandshakeMsg msg)
        {
            if (msg == null) return;

            string ourVersion = capi.ModLoader.GetMod("canevents")?.Info?.Version ?? "?";
            Send(new ProtocolHandshakeMsg { ProtocolVersion = CanEventsProtocol.Version, ModVersion = ourVersion });

            ProtocolMismatch = msg.ProtocolVersion != CanEventsProtocol.Version;
            if (!ProtocolMismatch) return;

            capi.Logger.Error("[canevents] Network protocol mismatch: this client is mod {0} (protocol v{1}), "
                + "the server is mod {2} (protocol v{3}). Install the server's version of the mod.",
                ourVersion, CanEventsProtocol.Version, msg.ModVersion ?? "?", msg.ProtocolVersion);
            capi.ShowChatMessage(Vintagestory.API.Config.Lang.Get("canevents:protocol-mismatch-client",
                ourVersion, CanEventsProtocol.Version, msg.ModVersion ?? "?", msg.ProtocolVersion));
        }

        // Rate-limits error logging per packet type.
        private readonly Dictionary<Type, long> lastHandlerErrorMs = new Dictionary<Type, long>();

        private const long HandlerErrorLogIntervalMs = 10000;

        private void LogHandlerFailure(Type packetType, Exception e)
        {
            long now = capi.ElapsedMilliseconds;
            if (lastHandlerErrorMs.TryGetValue(packetType, out long last)
                && now - last < HandlerErrorLogIntervalMs) return;

            lastHandlerErrorMs[packetType] = now;
            capi.Logger.Error("[canevents] Handler for {0} failed; further reports are muted for {1}s: {2}",
                packetType.Name, HandlerErrorLogIntervalMs / 1000, e);
        }

        public string MainDialogHotkey { get; private set; }
        public event Action<string> OnMainDialogHotkeyReceived;

        private void OnClientConfigSync(ClientConfigSyncMsg msg)
        {
            if (msg == null) return;
            MainDialogHotkey = msg.MainDialogHotkey;
            SpectatePrivileged = msg.MaySpectate;
            // Only ever enables tracing, never disables it.
            if (msg.Diagnostics) ClientTrace.SetEnabled(true);
            OnMainDialogHotkeyReceived?.Invoke(msg.MainDialogHotkey);
        }

        public readonly ServerState<LoadoutDefaultsListMsg> LoadoutDefaults = new ServerState<LoadoutDefaultsListMsg>();

        public readonly ServerState<EventRewardConfigMsg> EventRewardConfig = new ServerState<EventRewardConfigMsg>();

        public readonly ServerState<HgSetupMsg> HgSetup = new ServerState<HgSetupMsg>();

        public readonly ServerState<MmConfigMsg> MmConfig = new ServerState<MmConfigMsg>();

        public void SendRequestEventRewardConfig(string eventCode)
            => Send(new RequestEventRewardConfigMsg { EventCode = eventCode });

        public void SendSaveEventRewardConfig(EventRewardConfigMsg msg)
            => Send(msg);

        public readonly ServerState<RewardLimitsMsg> RewardLimits = new ServerState<RewardLimitsMsg>();

        public void SendRequestRewardLimits()
            => Send(new RequestRewardLimitsMsg());

        public void SendSaveRewardLimits(RewardLimitsMsg msg)
            => Send(msg);

        public readonly ServerState<ServerSettingsMsg> ServerSettings = new ServerState<ServerSettingsMsg>();

        public void SendRequestServerSettings()
            => Send(new RequestServerSettingsMsg());

        // Server replies with a toast and the full list.
        public void SendSetServerSetting(string key, string value)
            => Send(new SetServerSettingMsg { Key = key, Value = value });

        public readonly ServerState<EventSettingsMsg> EventSettings = new ServerState<EventSettingsMsg>();

        public void SendRequestEventSettings(string eventCode)
            => Send(new RequestEventSettingsMsg { EventCode = eventCode });

        // Server replies with a toast and the event's full list.
        public void SendSetEventSetting(string eventCode, string behavior, string key, string value)
            => Send(new SetEventSettingMsg { EventCode = eventCode, Behavior = behavior, Key = key, Value = value });

        public void RequestLoadoutDefaults()
        {
            Send(new RequestLoadoutDefaultsMsg());
        }

        public event Action<LoadoutPickRequestMsg> OnLoadoutPickRequested;

        private void OnLoadoutPickRequest(LoadoutPickRequestMsg msg) => OnLoadoutPickRequested?.Invoke(msg);

        public void SendLoadoutPickResponse(long eventId, string eventCode, string setName, bool makeDefault, string registrationGuid = null)
        {
            Send(new LoadoutPickResponseMsg
            {
                EventId = eventId,
                EventCode = eventCode,
                SetName = setName,
                MakeDefault = makeDefault,
                RegistrationGuid = registrationGuid
            });
        }

        public void SendClearLoadoutDefault(string eventCode)
        {
            Send(new ClearLoadoutDefaultMsg { EventCode = eventCode });
        }

        public readonly ServerState<TplSessionStateMsg> TplSession = new ServerState<TplSessionStateMsg>();

        public void SendTplAction(TplAction action, string templateCode = null)
        {
            Send(new TplActionMsg { Action = action, TemplateCode = templateCode });
        }

        public void SendJoinRequest(string eventCode, string loadoutSet = null)
        {
            Send(new JoinEventRequestMsg { EventCode = eventCode, LoadoutSet = loadoutSet });
        }

        public void SendLeaveRequest()
        {
            Send(new LeaveEventRequestMsg());
        }

        public void SendLeaveRequest(string eventCode)
        {
            Send(new LeaveEventRequestMsg { EventCode = eventCode });
        }

        public void RequestEventList()
        {
            Send(new RequestEventListMsg());
        }

        public readonly ServerState<AdminEventListMsg> AdminEventList = new ServerState<AdminEventListMsg>();

        public readonly ServerState<ScheduleStateMsg> ScheduleState = new ServerState<ScheduleStateMsg>();

        public void RequestAdminEventList()
        {
            Send(new RequestAdminEventListMsg());
        }

        public void SendToggleEventEnabled(string code)
        {
            Send(new ToggleEventEnabledMsg { Code = code });
        }

        public void RequestScheduleState()
        {
            Send(new RequestScheduleStateMsg());
        }

        public void SendAdminAction(AdminAction action, string eventCode = null, int intValue = 0,
            BotMatchMode botMode = BotMatchMode.Play)
        {
            Send(new AdminActionMsg
            {
                Action = action,
                EventCode = eventCode,
                IntValue = intValue,
                BotMode = botMode
            });
        }

        public void RequestTemplateList()
        {
            Send(new RequestTemplateListMsg());
        }

        public void SendPlaceTemplate(string templateCode)
        {
            Send(new PlaceTemplateMsg { TemplateCode = templateCode });
        }

        public readonly ServerState<MatchHistoryMsg> MatchHistoryData = new ServerState<MatchHistoryMsg>();

        public void RequestMatchHistory()
        {
            Send(new RequestMatchHistoryMsg());
        }

        // One state per ItemSetKinds kind, so the loadout and outfit editors don't clash.
        private readonly Dictionary<string, ServerState<LoadoutListMsg>> itemSetLists
            = new Dictionary<string, ServerState<LoadoutListMsg>>();

        public ServerState<LoadoutListMsg> ItemSetList(string kind = null)
        {
            kind = EventManagers.ItemSetKinds.Normalize(kind);
            if (!itemSetLists.TryGetValue(kind, out var state))
            {
                state = new ServerState<LoadoutListMsg>();
                itemSetLists[kind] = state;
            }
            return state;
        }

        public ServerState<LoadoutListMsg> LoadoutList => ItemSetList(EventManagers.ItemSetKinds.Loadout);

        public void RequestLoadoutList(string kind = null)
        {
            Send(new RequestLoadoutListMsg { Kind = kind });
        }

        public void SendDeleteLoadoutSet(string eventCode, string setName, string kind = null)
        {
            Send(new DeleteLoadoutSetMsg
            {
                EventCode = eventCode, SetName = setName, Kind = kind
            });
        }

        public void SendImportInlineSets(string eventCode, string kind = null)
        {
            Send(new ImportInlineSetsMsg { EventCode = eventCode, Kind = kind });
        }

        public void SendSaveLoadout(string eventCode, string setName, string[] codes, int[] quantities,
            string kind = null, string[] ids = null, UpgradeStepMsg[] upgrades = null)
        {
            Send(new SaveLoadoutMsg
            {
                EventCode = eventCode,
                SetName = setName,
                ItemCodes = codes,
                ItemQuantities = quantities,
                ItemIds = ids,
                Upgrades = upgrades,
                Kind = kind
            });
        }

        private void OnStateUpdate(EventStateUpdateMsg msg)
        {
            EventStates[msg.EventId] = msg;

            if (msg.EventState == EventManagers.EventState.Running
                || msg.EventState == EventManagers.EventState.LocationFound
                || msg.EventState == EventManagers.EventState.SearchingForLocation)
            {
                ActiveEventId = msg.EventId;
            }

            if (msg.EventState == EventManagers.EventState.Finished)
            {
                EventStates.Remove(msg.EventId);
                Scoreboards.Remove(msg.EventId);
                KillFeeds.Remove(msg.EventId);
                DropShopFor(msg.EventId);
                if (ActiveEventId == msg.EventId)
                    ActiveEventId = 0;
            }

            OnStateUpdated?.Invoke();
        }

        private void OnScoreUpdate(ScoreboardUpdateMsg msg)
        {
            Scoreboards[msg.EventId] = msg;
            OnScoreboardUpdated?.Invoke();
        }

        private void OnKillFeed(KillFeedMsg msg)
        {
            if (!KillFeeds.TryGetValue(msg.EventId, out var feed))
            {
                feed = new List<KillFeedMsg>();
                KillFeeds[msg.EventId] = feed;
            }
            feed.Add(msg);
            if (feed.Count > 10)
            {
                feed.RemoveAt(0);
            }
            OnKillFeedReceived?.Invoke(msg);
        }

        private void OnResult(EventResultMsg msg)
        {
            LastResult = msg;
            EventStates.Remove(msg.EventId);
            Scoreboards.Remove(msg.EventId);
            KillFeeds.Remove(msg.EventId);
            DropShopFor(msg.EventId);
            if (ActiveEventId == msg.EventId)
                ActiveEventId = 0;
            OnResultReceived?.Invoke(msg);
        }

        public readonly ServerState<SchematicListMsg> SchematicList = new ServerState<SchematicListMsg>();

        public void RequestSchematicList()
        {
            Send(new RequestSchematicListMsg());
        }

        public void SendCreateTemplateFromSchematic(string schematicName, string templateCode, string[] placeTypes, bool onDemand)
        {
            Send(new CreateTemplateFromSchematicMsg
            {
                SchematicName = schematicName,
                TemplateCode = templateCode,
                PlaceTypes = placeTypes,
                OnDemand = onDemand
            });
        }

        public void RequestLeaderboard(string eventCode = null)
        {
            Send(new RequestLeaderboardMsg { EventCode = eventCode });
        }

        public void RequestPlayerAccess()
        {
            Send(new RequestPlayerAccessMsg());
        }

        // Pass a uid (list row) or a typed name.
        public void SendPlayerAccessAction(PlayerAccessAction action, string uid = null, string name = null)
        {
            Send(new PlayerAccessActionMsg
            {
                Action = action,
                PlayerUid = uid,
                PlayerName = name
            });
        }
    }
}
