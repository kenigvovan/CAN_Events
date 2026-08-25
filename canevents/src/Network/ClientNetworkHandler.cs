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
    // Client-side network handler, split across ClientNetworkHandler.*.cs by topic, mirroring the
    // server's NetworkHandler.*.cs. This file owns the channel, message registration and live
    // match state; each topic file keeps its cached state, handler and senders together.

    public partial class ClientNetworkHandler
    {
        private ICoreClientAPI capi;
        private IClientNetworkChannel clientChannel;

        // Client-side state consumed by the GUI, keyed by EventId.
        private Dictionary<long, EventStateUpdateMsg> EventStates = new Dictionary<long, EventStateUpdateMsg>();
        private Dictionary<long, ScoreboardUpdateMsg> Scoreboards = new Dictionary<long, ScoreboardUpdateMsg>();
        private Dictionary<long, List<KillFeedMsg>> KillFeeds = new Dictionary<long, List<KillFeedMsg>>();
        public long ActiveEventId { get; private set; }

        // Delegate to the active event, so the HUD needs no per-event lookups.
        public EventStateUpdateMsg CurrentEventState =>
            ActiveEventId != 0 && EventStates.TryGetValue(ActiveEventId, out var s) ? s : null;
        public ScoreboardUpdateMsg CurrentScoreboard =>
            ActiveEventId != 0 && Scoreboards.TryGetValue(ActiveEventId, out var s) ? s : null;
        public List<KillFeedMsg> KillFeed =>
            ActiveEventId != 0 && KillFeeds.TryGetValue(ActiveEventId, out var f) ? f : new List<KillFeedMsg>();

        public EventResultMsg LastResult { get; private set; }

        // Server-fed snapshots the GUI recomposes from. Each carries its own Changed event -
        // see ServerState<T> for why they are not property+event pairs.
        public readonly ServerState<EventListMsg> EventList = new ServerState<EventListMsg>();

        /// <summary>Client clock when the event list last arrived. The list is pushed on changes, not
        /// on a timer, so anything time-based in it has to be aged against this.</summary>
        private long eventListReceivedMs;

        /// <summary>Seconds since the event list arrived, for ageing the figures inside it.</summary>
        public double SecondsSinceEventList()
            => eventListReceivedMs == 0 ? 0 : (capi.ElapsedMilliseconds - eventListReceivedMs) / 1000.0;

        /// <summary>The queue entry this player is standing in, or null. Only one at a time: joining a
        /// second queue is refused server-side.</summary>
        public EventListEntryMsg QueuedEntry()
        {
            var events = EventList.Value?.Events;
            if (events == null) return null;

            foreach (var e in events)
                if (e.IsQueued && e.EventId == 0) return e;
            return null;
        }

        /// <summary>Who may spectate and who is barred from the queue; admin panel only.</summary>
        public readonly ServerState<PlayerAccessListMsg> PlayerAccess = new ServerState<PlayerAccessListMsg>();
        public readonly ServerState<TemplateListMsg> TemplateList = new ServerState<TemplateListMsg>();
        public readonly ServerState<LeaderboardMsg> Leaderboard = new ServerState<LeaderboardMsg>();

        public event Action OnStateUpdated;
        public event Action OnScoreboardUpdated;
        public event Action<KillFeedMsg> OnKillFeedReceived;
        public event Action<EventResultMsg> OnResultReceived;

        /// <summary>uid -> team index of the flag that player carries; empty outside a CTF match.
        /// Replaced wholesale rather than mutated - the flag renderer reads it every frame and a
        /// half-updated dictionary would show a flag on someone who just scored.</summary>
        public IReadOnlyDictionary<string, int> CtfCarriers { get; private set; }
            = new Dictionary<string, int>();

        /// <summary>Latest storm-zone segment + arrival time (client clock), so the dome renderer can
        /// interpolate the radius locally. Null (or Active=false) outside a Hunger Games match.</summary>
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

            clientChannel = capi.Network.RegisterChannel(ServerNetworkHandler.ChannelName);
            CanEventsProtocol.RegisterAll(clientChannel);

            SetHandler<ScheduleStateMsg>(ScheduleState.Set);
            SetHandler<ClientConfigSyncMsg>(OnClientConfigSync);
            SetHandler<EventRewardConfigMsg>(EventRewardConfig.Set);
            SetHandler<RewardLimitsMsg>(RewardLimits.Set);
            SetHandler<AdminEventListMsg>(AdminEventList.Set);
            SetHandler<LoadoutListMsg>(msg => ItemSetList(msg?.Kind).Set(msg));
            SetHandler<PlatformEditStateMsg>(PlatformEditState.Set);
            SetHandler<MatchHistoryMsg>(MatchHistoryData.Set);
            SetHandler<SchematicListMsg>(SchematicList.Set);
            SetHandler<CtfCarriersMsg>(OnCtfCarriers);
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
            SetHandler<PlaceOperationResultMsg>(OnPlaceResult);
            SetHandler<TemplateListMsg>(TemplateList.Set);
            SetHandler<LeaderboardMsg>(Leaderboard.Set);
            SetHandler<TplSessionStateMsg>(TplSession.Set);
            SetHandler<PartyStateMsg>(PartyState.Set);
            SetHandler<PartyOperationResultMsg>(OnPartyResult);
            SetHandler<LoadoutPickRequestMsg>(OnLoadoutPickRequest);
            SetHandler<LoadoutDefaultsListMsg>(LoadoutDefaults.Set);
            SetHandler<TournamentListMsg>(TournamentList.Set);
            SetHandler<TournamentBracketMsg>(TournamentBracket.Set);
            SetHandler<TournamentReadyRequestMsg>(OnTournamentReadyRequest);
            SetHandler<TournamentReadyClosedMsg>(HandleTournamentReadyClosed);
            SetHandler<EventInviteMsg>(OnEventInvite);
            SetHandler<EventInviteClosedMsg>(HandleEventInviteClosed);
            SetHandler<PlayerAccessListMsg>(PlayerAccess.Set);
            RegisterSpectateHandlers();
            RegisterDuelHandlers();
            RegisterReadyHandlers();
        }

        /// <summary>
        /// Registers a packet handler with its errors contained. Same reasoning as the server side:
        /// the engine invokes handlers straight, so a throw here unwinds into the client's packet
        /// loop. The data is trusted here - it comes from the server - but a version mismatch or a
        /// half-populated snapshot should degrade one panel, not drop the player out of the game.
        /// </summary>
        private void SetHandler<T>(NetworkServerMessageHandler<T> handler)
            => clientChannel.SetMessageHandler<T>(msg =>
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

        // Muted per packet type, so a server looping a packet the client cannot digest does not fill
        // the log with the same stack trace.
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

        /// <summary>Last admin-configured default key for the main dialog (null until received).</summary>
        public string MainDialogHotkey { get; private set; }
        /// <summary>Fires when the server pushes the main-dialog default key on join.</summary>
        public event Action<string> OnMainDialogHotkeyReceived;

        private void OnClientConfigSync(ClientConfigSyncMsg msg)
        {
            if (msg == null) return;
            MainDialogHotkey = msg.MainDialogHotkey;
            // Known from join onward, so the browser can offer the Watch button before the player has
            // ever asked to spectate. Refreshed by every spectate reply after that.
            SpectatePrivileged = msg.MaySpectate;
            OnMainDialogHotkeyReceived?.Invoke(msg.MainDialogHotkey);
        }

        public readonly ServerState<LoadoutDefaultsListMsg> LoadoutDefaults = new ServerState<LoadoutDefaultsListMsg>();

        /// <summary>Last reward/entry-fee config the server sent for the admin editor (null until requested).</summary>
        public readonly ServerState<EventRewardConfigMsg> EventRewardConfig = new ServerState<EventRewardConfigMsg>();

        // Latest HG setup snapshot for the place the panel last asked about (tiers, overview, spots).
        public readonly ServerState<HgSetupMsg> HgSetup = new ServerState<HgSetupMsg>();

        // Latest Murder Mystery settings for the place the panel last asked about.
        public readonly ServerState<MmConfigMsg> MmConfig = new ServerState<MmConfigMsg>();

        public void SendRequestEventRewardConfig(string eventCode)
            => clientChannel.SendPacket(new RequestEventRewardConfigMsg { EventCode = eventCode });

        public void SendSaveEventRewardConfig(EventRewardConfigMsg msg)
            => clientChannel.SendPacket(msg);

        /// <summary>Last global reward/anti-farm limits the server sent (null until requested).</summary>
        public readonly ServerState<RewardLimitsMsg> RewardLimits = new ServerState<RewardLimitsMsg>();

        public void SendRequestRewardLimits()
            => clientChannel.SendPacket(new RequestRewardLimitsMsg());

        public void SendSaveRewardLimits(RewardLimitsMsg msg)
            => clientChannel.SendPacket(msg);

        public void RequestLoadoutDefaults()
        {
            clientChannel.SendPacket(new RequestLoadoutDefaultsMsg());
        }

        // Match-start loadout pick
        public LoadoutPickRequestMsg PendingLoadoutPick { get; private set; }
        public event Action<LoadoutPickRequestMsg> OnLoadoutPickRequested;

        private void OnLoadoutPickRequest(LoadoutPickRequestMsg msg)
        {
            PendingLoadoutPick = msg;
            OnLoadoutPickRequested?.Invoke(msg);
        }

        public void SendLoadoutPickResponse(long eventId, string eventCode, string setName, bool makeDefault, string registrationGuid = null)
        {
            clientChannel.SendPacket(new LoadoutPickResponseMsg
            {
                EventId = eventId,
                EventCode = eventCode,
                SetName = setName,
                MakeDefault = makeDefault,
                RegistrationGuid = registrationGuid
            });
            PendingLoadoutPick = null;
        }

        public void SendClearLoadoutDefault(string eventCode)
        {
            clientChannel.SendPacket(new ClearLoadoutDefaultMsg { EventCode = eventCode });
        }

        /// <summary>Locally dismisses the match-start class picker without sending a response, for when
        /// the server-side timeout already elapsed or the player closed the window.</summary>
        public void DismissLoadoutPick() => PendingLoadoutPick = null;

        public readonly ServerState<TplSessionStateMsg> TplSession = new ServerState<TplSessionStateMsg>();

        public void SendTplAction(TplAction action, string templateCode = null)
        {
            clientChannel.SendPacket(new TplActionMsg { Action = action, TemplateCode = templateCode });
        }

        // Send methods (client → server)

        public void SendJoinRequest(string eventCode, string loadoutSet = null)
        {
            clientChannel.SendPacket(new JoinEventRequestMsg { EventCode = eventCode, LoadoutSet = loadoutSet });
        }

        public void SendLeaveRequest()
        {
            clientChannel.SendPacket(new LeaveEventRequestMsg());
        }

        public void SendLeaveRequest(string eventCode)
        {
            clientChannel.SendPacket(new LeaveEventRequestMsg { EventCode = eventCode });
        }

        public void RequestEventList()
        {
            clientChannel.SendPacket(new RequestEventListMsg());
        }

        // Admin event enable/disable
        public readonly ServerState<AdminEventListMsg> AdminEventList = new ServerState<AdminEventListMsg>();

        public readonly ServerState<ScheduleStateMsg> ScheduleState = new ServerState<ScheduleStateMsg>();

        public void RequestAdminEventList()
        {
            clientChannel.SendPacket(new RequestAdminEventListMsg());
        }

        public void SendToggleEventEnabled(string code)
        {
            clientChannel.SendPacket(new ToggleEventEnabledMsg { Code = code });
        }

        public void RequestScheduleState()
        {
            clientChannel.SendPacket(new RequestScheduleStateMsg());
        }

        public void SendAdminAction(AdminAction action, string eventCode = null, int intValue = 0,
            BotMatchMode botMode = BotMatchMode.Play)
        {
            clientChannel.SendPacket(new AdminActionMsg
            {
                Action = action,
                EventCode = eventCode,
                IntValue = intValue,
                BotMode = botMode
            });
        }

        public void RequestTemplateList()
        {
            clientChannel.SendPacket(new RequestTemplateListMsg());
        }

        public void SendPlaceTemplate(string templateCode)
        {
            clientChannel.SendPacket(new PlaceTemplateMsg { TemplateCode = templateCode });
        }

        public readonly ServerState<MatchHistoryMsg> MatchHistoryData = new ServerState<MatchHistoryMsg>();

        public void RequestMatchHistory()
        {
            clientChannel.SendPacket(new RequestMatchHistoryMsg());
        }

        // One list per item-set kind (loadouts, outfits - see ItemSetKinds), each its own
        // ServerState so the admin panel's two editors never overwrite each other's list.
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

        /// <summary>The loadout list, for the many call sites that only ever want that one.</summary>
        public ServerState<LoadoutListMsg> LoadoutList => ItemSetList(EventManagers.ItemSetKinds.Loadout);

        public void RequestLoadoutList(string kind = null)
        {
            clientChannel.SendPacket(new RequestLoadoutListMsg { Kind = kind });
        }

        public void SendSaveLoadoutFromInv(string eventCode, string setName, string kind = null)
        {
            clientChannel.SendPacket(new SaveLoadoutFromInvMsg
            {
                EventCode = eventCode, SetName = setName, Kind = kind
            });
        }

        public void SendDeleteLoadoutSet(string eventCode, string setName, string kind = null)
        {
            clientChannel.SendPacket(new DeleteLoadoutSetMsg
            {
                EventCode = eventCode, SetName = setName, Kind = kind
            });
        }

        public void SendImportInlineSets(string eventCode, string kind = null)
        {
            clientChannel.SendPacket(new ImportInlineSetsMsg { EventCode = eventCode, Kind = kind });
        }

        public void SendSaveLoadout(string eventCode, string setName, string[] codes, int[] quantities,
            string kind = null)
        {
            clientChannel.SendPacket(new SaveLoadoutMsg
            {
                EventCode = eventCode,
                SetName = setName,
                ItemCodes = codes,
                ItemQuantities = quantities,
                Kind = kind
            });
        }

        // Handlers

        private void OnStateUpdate(EventStateUpdateMsg msg)
        {
            EventStates[msg.EventId] = msg;

            // Track the event the player is actively participating in.
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
            if (ActiveEventId == msg.EventId)
                ActiveEventId = 0;
            OnResultReceived?.Invoke(msg);
        }

        public readonly ServerState<SchematicListMsg> SchematicList = new ServerState<SchematicListMsg>();

        public void RequestSchematicList()
        {
            clientChannel.SendPacket(new RequestSchematicListMsg());
        }

        public void SendCreateTemplateFromSchematic(string schematicName, string templateCode, string[] placeTypes, bool onDemand)
        {
            clientChannel.SendPacket(new CreateTemplateFromSchematicMsg
            {
                SchematicName = schematicName,
                TemplateCode = templateCode,
                PlaceTypes = placeTypes,
                OnDemand = onDemand
            });
        }

        public void RequestLeaderboard(string eventCode = null)
        {
            clientChannel.SendPacket(new RequestLeaderboardMsg { EventCode = eventCode });
        }

        public void RequestPlayerAccess()
        {
            clientChannel.SendPacket(new RequestPlayerAccessMsg());
        }

        /// <summary>Change one player's access. Either a uid (a row in the list) or a typed name.</summary>
        public void SendPlayerAccessAction(PlayerAccessAction action, string uid = null, string name = null)
        {
            clientChannel.SendPacket(new PlayerAccessActionMsg
            {
                Action = action,
                PlayerUid = uid,
                PlayerName = name
            });
        }
    }
}
