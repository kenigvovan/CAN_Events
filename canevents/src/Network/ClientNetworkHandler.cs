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

            clientChannel.SetMessageHandler<ScheduleStateMsg>(ScheduleState.Set);
            clientChannel.SetMessageHandler<ClientConfigSyncMsg>(OnClientConfigSync);
            clientChannel.SetMessageHandler<EventRewardConfigMsg>(EventRewardConfig.Set);
            clientChannel.SetMessageHandler<RewardLimitsMsg>(RewardLimits.Set);
            clientChannel.SetMessageHandler<AdminEventListMsg>(AdminEventList.Set);
            clientChannel.SetMessageHandler<LoadoutListMsg>(LoadoutList.Set);
            clientChannel.SetMessageHandler<PlatformEditStateMsg>(PlatformEditState.Set);
            clientChannel.SetMessageHandler<MatchHistoryMsg>(MatchHistoryData.Set);
            clientChannel.SetMessageHandler<SchematicListMsg>(SchematicList.Set);
            clientChannel.SetMessageHandler<CtfCarriersMsg>(OnCtfCarriers);
            clientChannel.SetMessageHandler<HgZoneStateMsg>(m => { HgZone = m; HgZoneReceivedMs = capi.ElapsedMilliseconds; });
            clientChannel.SetMessageHandler<HgSetupMsg>(HgSetup.Set);
            clientChannel.SetMessageHandler<EventStateUpdateMsg>(OnStateUpdate);
            clientChannel.SetMessageHandler<ScoreboardUpdateMsg>(OnScoreUpdate);
            clientChannel.SetMessageHandler<KillFeedMsg>(OnKillFeed);
            clientChannel.SetMessageHandler<EventResultMsg>(OnResult);
            clientChannel.SetMessageHandler<EventListMsg>(EventList.Set);
            clientChannel.SetMessageHandler<PlaceListMsg>(OnPlaceList);
            clientChannel.SetMessageHandler<PlaceOperationResultMsg>(OnPlaceResult);
            clientChannel.SetMessageHandler<TemplateListMsg>(TemplateList.Set);
            clientChannel.SetMessageHandler<LeaderboardMsg>(Leaderboard.Set);
            clientChannel.SetMessageHandler<TplSessionStateMsg>(TplSession.Set);
            clientChannel.SetMessageHandler<PartyStateMsg>(PartyState.Set);
            clientChannel.SetMessageHandler<PartyOperationResultMsg>(OnPartyResult);
            clientChannel.SetMessageHandler<LoadoutPickRequestMsg>(OnLoadoutPickRequest);
            clientChannel.SetMessageHandler<LoadoutDefaultsListMsg>(LoadoutDefaults.Set);
            clientChannel.SetMessageHandler<TournamentListMsg>(TournamentList.Set);
            clientChannel.SetMessageHandler<TournamentBracketMsg>(TournamentBracket.Set);
            clientChannel.SetMessageHandler<TournamentReadyRequestMsg>(OnTournamentReadyRequest);
            clientChannel.SetMessageHandler<TournamentReadyClosedMsg>(HandleTournamentReadyClosed);
            clientChannel.SetMessageHandler<EventInviteMsg>(OnEventInvite);
            clientChannel.SetMessageHandler<EventInviteClosedMsg>(HandleEventInviteClosed);
        }

        /// <summary>Last admin-configured default key for the main dialog (null until received).</summary>
        public string MainDialogHotkey { get; private set; }
        /// <summary>Fires when the server pushes the main-dialog default key on join.</summary>
        public event Action<string> OnMainDialogHotkeyReceived;

        private void OnClientConfigSync(ClientConfigSyncMsg msg)
        {
            if (msg == null) return;
            MainDialogHotkey = msg.MainDialogHotkey;
            OnMainDialogHotkeyReceived?.Invoke(msg.MainDialogHotkey);
        }

        public readonly ServerState<LoadoutDefaultsListMsg> LoadoutDefaults = new ServerState<LoadoutDefaultsListMsg>();

        /// <summary>Last reward/entry-fee config the server sent for the admin editor (null until requested).</summary>
        public readonly ServerState<EventRewardConfigMsg> EventRewardConfig = new ServerState<EventRewardConfigMsg>();

        // Latest HG setup snapshot for the place the panel last asked about (tiers, overview, spots).
        public readonly ServerState<HgSetupMsg> HgSetup = new ServerState<HgSetupMsg>();

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

        public void SendAdminAction(AdminAction action, string eventCode = null, int intValue = 0)
        {
            clientChannel.SendPacket(new AdminActionMsg { Action = action, EventCode = eventCode, IntValue = intValue });
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

        public readonly ServerState<LoadoutListMsg> LoadoutList = new ServerState<LoadoutListMsg>();

        public void RequestLoadoutList()
        {
            clientChannel.SendPacket(new RequestLoadoutListMsg());
        }

        public void SendSaveLoadoutFromInv(string eventCode, string setName)
        {
            clientChannel.SendPacket(new SaveLoadoutFromInvMsg { EventCode = eventCode, SetName = setName });
        }

        public void SendDeleteLoadoutSet(string eventCode, string setName)
        {
            clientChannel.SendPacket(new DeleteLoadoutSetMsg { EventCode = eventCode, SetName = setName });
        }

        public void SendImportInlineSets(string eventCode)
        {
            clientChannel.SendPacket(new ImportInlineSetsMsg { EventCode = eventCode });
        }

        public void SendSaveLoadout(string eventCode, string setName, string[] codes, int[] quantities)
        {
            clientChannel.SendPacket(new SaveLoadoutMsg
            {
                EventCode = eventCode,
                SetName = setName,
                ItemCodes = codes,
                ItemQuantities = quantities
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
    }
}
