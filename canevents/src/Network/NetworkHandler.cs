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
    public partial class ServerNetworkHandler
    {
        // Limits for client-supplied data.
        private const int MaxLoadoutSetName = 64;
        private const int MaxLoadoutItems = 100;
        private const int MaxLoadoutItemCodeLen = 256;
        private const int MaxItemQuantity = 100;

        private ICoreServerAPI sapi;

        private IServerNetworkChannel serverChannel;

        private IServerNetworkChannel adminChannel;

        private CANEventManager eventManager;
        private readonly PlayerDelegate _onPlayerNowPlaying;
        private readonly PlayerDelegate _onPlayerDisconnect;

        public Scoring.MatchHistory MatchHistory { get; set; }

        // From Config.mainDialogHotkey, sent to clients on join.
        public string MainDialogHotkey { get; set; } = "K";

        // From Config.diagnosticsLogging. Turns on client trace logging for players who join.
        public bool DiagnosticsLogging { get; set; }

        public ServerNetworkHandler(ICoreServerAPI sapi, CANEventManager eventManager)
        {
            this.sapi = sapi;
            this.eventManager = eventManager;

            serverChannel = sapi.Network.RegisterChannel(CanEventsProtocol.MainChannelName);
            CanEventsProtocol.Register(serverChannel, ProtocolChannel.Main);
            adminChannel = sapi.Network.RegisterChannel(CanEventsProtocol.AdminChannelName);
            CanEventsProtocol.Register(adminChannel, ProtocolChannel.Admin);
            sapi.World.Logger.Notification(CanEventsProtocol.Summary());

            _onPlayerNowPlaying = OnPlayerNowPlaying;
            _onPlayerDisconnect = player =>
            {
                ForgetClientProtocol(player);
                ForgetThrottles(player);
                OnPlayerDisconnect(player);
            };
            sapi.Event.PlayerNowPlaying  += _onPlayerNowPlaying;
            sapi.Event.PlayerDisconnect  += _onPlayerDisconnect;

            Handle<ProtocolHandshakeMsg>(ProtocolChannel.Main, false, OnClientHandshake);

            const ProtocolChannel admin = ProtocolChannel.Admin;
            Handle<RequestLoadoutListMsg>(admin, true, OnRequestLoadoutList);
            Handle<SaveLoadoutMsg>(admin, true, OnSaveLoadout);
            Handle<DeleteLoadoutSetMsg>(admin, true, OnDeleteLoadoutSet);
            Handle<ImportInlineSetsMsg>(admin, true, OnImportInlineSets);

            Handle<SetCagePosMsg>(admin, true, OnSetCagePos);
            Handle<ClearCagePosMsg>(admin, true, OnClearCagePos);
            Handle<PlatformEditStartMsg>(admin, true, OnPlatformEditStart);
            Handle<PlatformEditStopMsg>(admin, true, OnPlatformEditStop);
            Handle<PlatformFillShapeMsg>(admin, true, OnPlatformFillShape);
            Handle<PlatformFloodFillMsg>(admin, true, OnPlatformFloodFill);
            Handle<RequestSchematicListMsg>(admin, true, OnRequestSchematicList);
            Handle<CreateTemplateFromSchematicMsg>(admin, true, OnCreateTemplateFromSchematic);
            Handle<RequestPlaceListMsg>(admin, true, OnPlaceListRequest);
            Handle<CreatePlaceMsg>(admin, true, OnCreatePlace);
            Handle<AddSpawnPointMsg>(admin, true, OnAddSpawnPoint);
            Handle<SetPlaceBoundsMsg>(admin, true, OnSetPlaceBounds);
            Handle<MoveSpawnPointMsg>(admin, true, OnMoveSpawnPoint);
            Handle<TeleportToPlaceMsg>(admin, true, OnTeleportToPlace);
            Handle<TeleportToHgLootSpotMsg>(admin, true, OnTeleportToHgLootSpot);
            Handle<RemoveSpawnPointMsg>(admin, true, OnRemoveSpawnPoint);
            Handle<DeletePlacesMsg>(admin, true, OnDeletePlaces);
            Handle<TogglePlaceMsg>(admin, true, OnTogglePlace);
            Handle<BulkEditPlacesMsg>(admin, true, OnBulkEditPlaces);
            Handle<EditPlaceInfoMsg>(admin, true, OnEditPlaceInfo);
            Handle<RequestTemplateListMsg>(admin, true, OnRequestTemplateList);
            Handle<PlaceTemplateMsg>(admin, true, OnPlaceTemplate);
            Handle<RemovePlaceWithRestoreMsg>(admin, true, OnRemovePlaceWithRestore);
            Handle<TplActionMsg>(admin, true, OnTplAction);
            Handle<RequestAdminEventListMsg>(admin, true, OnRequestAdminEventList);
            Handle<ToggleEventEnabledMsg>(admin, true, OnToggleEventEnabled);
            Handle<AdminActionMsg>(admin, true, OnAdminAction);
            Handle<RequestScheduleStateMsg>(admin, true, OnRequestScheduleState);
            Handle<RequestEventRewardConfigMsg>(admin, true, OnRequestEventRewardConfig);
            Handle<EventRewardConfigMsg>(admin, true, OnSaveEventRewardConfig);
            Handle<RequestRewardLimitsMsg>(admin, true, OnRequestRewardLimits);
            Handle<RewardLimitsMsg>(admin, true, OnSaveRewardLimits);
            Handle<SetCtfPostMsg>(admin, true, OnSetCtfPost);
            Handle<ClearCtfPostsMsg>(admin, true, OnClearCtfPosts);
            Handle<SetParkourCheckpointMsg>(admin, true, OnSetParkourCheckpoint);
            Handle<RemoveParkourCheckpointMsg>(admin, true, OnRemoveParkourCheckpoint);
            Handle<SetParkourCheckpointIndexMsg>(admin, true, OnSetParkourCheckpointIndex);
            Handle<SetParkourLivesMsg>(admin, true, OnSetParkourLives);
            Handle<SetKothHillMsg>(admin, true, OnSetKothHill);
            Handle<SetWaitingRoomMsg>(admin, true, OnSetWaitingRoom);
            Handle<PadEditMsg>(admin, true, OnPadEdit);
            Handle<SetHgZoneMsg>(admin, true, OnSetHgZone);
            Handle<AddHgLootMsg>(admin, true, OnAddHgLoot);
            Handle<ClearHgLootMsg>(admin, true, OnClearHgLoot);
            Handle<RequestHgSetupMsg>(admin, true, OnRequestHgSetup);
            Handle<RemoveHgLootMsg>(admin, true, OnRemoveHgLoot);
            Handle<SetHgLootTierMsg>(admin, true, OnSetHgLootTier);
            Handle<GenerateHgLayoutMsg>(admin, true, OnGenerateHgLayout);
            Handle<HgPreviewMsg>(admin, true, OnHgPreview);
            Handle<SaveHgConfigMsg>(admin, true, OnSaveHgConfig);
            Handle<AddMmPickupMsg>(admin, true, OnAddMmPickup);
            Handle<ClearMmPickupsMsg>(admin, true, OnClearMmPickups);
            Handle<RemoveMmPickupMsg>(admin, true, OnRemoveMmPickup);
            Handle<RequestMmConfigMsg>(admin, true, OnRequestMmConfig);
            Handle<SaveMmConfigMsg>(admin, true, OnSaveMmConfig);
            Handle<TournamentAdminActionMsg>(admin, true, OnTournamentAdminAction);

            RegisterAccessHandlers();

            RegisterInventoryAuditHandlers();

            RegisterServerSettingsHandlers();

            RegisterEventSettingsHandlers();

            const ProtocolChannel main = ProtocolChannel.Main;
            Handle<JoinEventRequestMsg>(main, false, OnJoinRequest);
            Handle<LeaveEventRequestMsg>(main, false, OnLeaveRequest);
            // Snapshots are rebuilt per request, so throttle them.
            Handle<RequestEventListMsg>(main, false, OnEventListRequest, minIntervalMs: 500);
            Handle<RequestMatchHistoryMsg>(main, false, OnRequestMatchHistory, minIntervalMs: 1000);
            Handle<RequestLeaderboardMsg>(main, false, OnLeaderboardRequest, minIntervalMs: 1000);
            Handle<PartyActionMsg>(main, false, OnPartyAction);
            Handle<LoadoutPickResponseMsg>(main, false, OnLoadoutPickResponse);
            Handle<ClearLoadoutDefaultMsg>(main, false, OnClearLoadoutDefault);
            Handle<RequestLoadoutDefaultsMsg>(main, false, OnRequestLoadoutDefaults);
            Handle<RequestTournamentListMsg>(main, false, OnRequestTournamentList, minIntervalMs: 1000);
            Handle<RequestTournamentBracketMsg>(main, false, OnRequestTournamentBracket);
            Handle<TournamentSignupMsg>(main, false, OnTournamentSignup);
            Handle<TournamentRenameMsg>(main, false, OnTournamentRename);
            Handle<TournamentLoadoutMsg>(main, false, OnTournamentLoadout);
            Handle<TournamentReadyResponseMsg>(main, false, OnTournamentReadyResponse);

            // Own spectate privilege, not controlserver.
            RegisterSpectateHandlers();

            RegisterDuelHandlers();

            RegisterReadyHandlers();
            RegisterTeamPickHandlers();
            RegisterShopHandlers();
        }

        private void OnPlayerNowPlaying(IServerPlayer player)
        {
            // Before anything else is sent.
            BeginProtocolCheck(player);

            if (_partyManager != null) SendPartyState(player);
            serverChannel.SendPacket(new ClientConfigSyncMsg
            {
                MainDialogHotkey = MainDialogHotkey,
                MaySpectate = MaySpectate(player),
                Diagnostics = DiagnosticsLogging
            }, player);
        }

        private IServerNetworkChannel ChannelOf(ProtocolChannel channel)
            => channel == ProtocolChannel.Admin ? adminChannel : serverChannel;

        // All server handlers go through here. Admin gate is enforced at registration.
        // ServerNetworkChannel.OnPacket doesn't catch, so exceptions are caught here.
        // minIntervalMs > 0: early requests are coalesced, the newest runs when the interval ends.
        private void Handle<T>(ProtocolChannel channel, bool adminOnly, NetworkClientMessageHandler<T> handler,
            int minIntervalMs = 0)
        {
            if (channel == ProtocolChannel.Admin && !adminOnly)
                throw new InvalidOperationException("[canevents] " + typeof(T).Name
                    + " is on the admin channel but was registered without the admin gate.");

            void Run(IServerPlayer player, T msg)
            {
                try
                {
                    if (!MayHandle(player, typeof(T), adminOnly)) return;
                    handler(player, msg);
                }
                catch (Exception e)
                {
                    LogHandlerFailure(typeof(T), player, e);
                }
            }

            ChannelOf(channel).SetMessageHandler<T>((player, msg) =>
            {
                if (minIntervalMs > 0 && player != null)
                {
                    string uid = player.PlayerUID;
                    // Player may have left by the time the deferred run fires.
                    if (!TryTakeThrottleSlot(uid, typeof(T), minIntervalMs, () =>
                        {
                            if (sapi.World.PlayerByUid(uid) is IServerPlayer p
                                && p.ConnectionState == EnumClientState.Playing)
                                Run(p, msg);
                        }))
                        return;
                }
                Run(player, msg);
            });
        }

        // Per player and packet type, cleared on disconnect.
        private sealed class ThrottleState
        {
            public long LastRunMs = long.MinValue / 2;
            public Action Pending;
        }

        private readonly Dictionary<string, Dictionary<Type, ThrottleState>> throttles =
            new Dictionary<string, Dictionary<Type, ThrottleState>>();

        // False = request parked as Pending, replacing any older one.
        private bool TryTakeThrottleSlot(string uid, Type type, int minIntervalMs, Action deferred)
        {
            long now = sapi.World.ElapsedMilliseconds;
            ThrottleState state;
            long wait;
            lock (throttles)
            {
                if (!throttles.TryGetValue(uid, out var byType))
                    throttles[uid] = byType = new Dictionary<Type, ThrottleState>();
                if (!byType.TryGetValue(type, out state))
                    byType[type] = state = new ThrottleState();

                wait = state.LastRunMs + minIntervalMs - now;
                if (wait <= 0 && state.Pending == null)
                {
                    state.LastRunMs = now;
                    return true;
                }

                bool alreadyScheduled = state.Pending != null;
                state.Pending = deferred;
                if (alreadyScheduled) return false;
            }

            sapi.Event.RegisterCallback(_ => FlushThrottle(uid, type, state), (int)Math.Max(1, wait));
            return false;
        }

        private void FlushThrottle(string uid, Type type, ThrottleState state)
        {
            Action run;
            lock (throttles)
            {
                // Player left (maybe rejoined) while this waited.
                if (!throttles.TryGetValue(uid, out var byType)
                    || !byType.TryGetValue(type, out var current) || current != state) return;
                run = state.Pending;
                state.Pending = null;
                if (run == null) return;
                state.LastRunMs = sapi.World.ElapsedMilliseconds;
            }
            run();
        }

        private void ForgetThrottles(IServerPlayer player)
        {
            if (player == null) return;
            lock (throttles) throttles.Remove(player.PlayerUID);
        }

        private bool MayHandle(IServerPlayer player, Type packetType, bool adminOnly)
        {
            if (player == null) return false;

            if (packetType == typeof(ProtocolHandshakeMsg)) return true;

            if (adminOnly)
            {
                if (!RequireAdmin(player)) return false;
                return RequireConfirmedProtocol(player, packetType);
            }

            // Unconfirmed is allowed: requests can arrive before the handshake.
            return !IsProtocolMismatched(player);
        }

        // Rate-limits error logging per packet type.
        private readonly Dictionary<Type, long> lastHandlerErrorMs = new Dictionary<Type, long>();

        private const long HandlerErrorLogIntervalMs = 10000;

        private void LogHandlerFailure(Type packetType, IServerPlayer player, Exception e)
        {
            long now = sapi.World.ElapsedMilliseconds;
            if (lastHandlerErrorMs.TryGetValue(packetType, out long last)
                && now - last < HandlerErrorLogIntervalMs) return;

            lastHandlerErrorMs[packetType] = now;
            sapi.World.Logger.Error("[canevents] Handler for {0} failed (from {1}); further reports of "
                + "this packet are muted for {2}s: {3}",
                packetType.Name, player?.PlayerName ?? "?", HandlerErrorLogIntervalMs / 1000, e);
        }

        // Defaults to the admin channel; player-facing handlers pass Main.
        private void Reply(IServerPlayer player, bool success, string message, long placeId = 0,
            ProtocolChannel channel = ProtocolChannel.Admin)
        {
            ChannelOf(channel).SendPacket(
                new PlaceOperationResultMsg { Success = success, Message = message, PlaceId = placeId }, player);
        }

        private void ReplyOk(IServerPlayer player, string message, long placeId = 0,
            ProtocolChannel channel = ProtocolChannel.Admin)
            => Reply(player, true, message, placeId, channel);

        private void ReplyFail(IServerPlayer player, string message,
            ProtocolChannel channel = ProtocolChannel.Admin)
            => Reply(player, false, message, 0, channel);

        public void Dispose()
        {
            sapi.Event.PlayerNowPlaying  -= _onPlayerNowPlaying;
            sapi.Event.PlayerDisconnect  -= _onPlayerDisconnect;

            UnwirePartyManager();
            UnhookPlatformEditing();
            eventManager?.HungerGamesSetup?.StopAllPreviews();
            RestoreAllCagePreviews();

            // Pending throttle callbacks then find nothing and do nothing.
            lock (throttles) throttles.Clear();
        }

    }

}
