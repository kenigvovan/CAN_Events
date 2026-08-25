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
        public const string ChannelName = "canevents";

        // Limits for client-supplied data (defense-in-depth; admin handlers are also privilege-gated).
        private const int MaxLoadoutSetName = 64;
        private const int MaxLoadoutItems = 100;
        private const int MaxLoadoutItemCodeLen = 256;
        private const int MaxItemQuantity = 100;
        private const int MaxCageDim = 128;
        private const int MinCageDim = 1;

        private ICoreServerAPI sapi;
        private IServerNetworkChannel serverChannel;
        private CANEventManager eventManager;
        private readonly PlayerDelegate _onPlayerNowPlaying;
        private readonly PlayerDelegate _onPlayerDisconnect;

        public Scoring.MatchHistory MatchHistory { get; set; }

        // Default key for the main Events Browser dialog, pushed to each client on join;
        // set from Config.mainDialogHotkey in StartServerSide.
        public string MainDialogHotkey { get; set; } = "K";

        public ServerNetworkHandler(ICoreServerAPI sapi, CANEventManager eventManager)
        {
            this.sapi = sapi;
            this.eventManager = eventManager;

            serverChannel = sapi.Network.RegisterChannel(ChannelName);
            CanEventsProtocol.RegisterAll(serverChannel);

            _onPlayerNowPlaying = OnPlayerNowPlaying;
            _onPlayerDisconnect = OnPlayerDisconnect;
            sapi.Event.PlayerNowPlaying  += _onPlayerNowPlaying;
            sapi.Event.PlayerDisconnect  += _onPlayerDisconnect;

            // Admin-only: the privilege is enforced here, at registration.
            SetAdminHandler<RequestLoadoutListMsg>(OnRequestLoadoutList);
            SetAdminHandler<SaveLoadoutFromInvMsg>(OnSaveLoadoutFromInv);
            SetAdminHandler<SaveLoadoutMsg>(OnSaveLoadout);
            SetAdminHandler<DeleteLoadoutSetMsg>(OnDeleteLoadoutSet);
            SetAdminHandler<ImportInlineSetsMsg>(OnImportInlineSets);

            SetAdminHandler<SetCagePosMsg>(OnSetCagePos);
            SetAdminHandler<ClearCagePosMsg>(OnClearCagePos);
            SetAdminHandler<PlatformEditStartMsg>(OnPlatformEditStart);
            SetAdminHandler<PlatformEditStopMsg>(OnPlatformEditStop);
            SetAdminHandler<PlatformFillShapeMsg>(OnPlatformFillShape);
            SetAdminHandler<PlatformFloodFillMsg>(OnPlatformFloodFill);
            SetAdminHandler<RequestSchematicListMsg>(OnRequestSchematicList);
            SetAdminHandler<CreateTemplateFromSchematicMsg>(OnCreateTemplateFromSchematic);
            SetAdminHandler<RequestPlaceListMsg>(OnPlaceListRequest);
            SetAdminHandler<CreatePlaceMsg>(OnCreatePlace);
            SetAdminHandler<AddSpawnPointMsg>(OnAddSpawnPoint);
            SetAdminHandler<SetPlaceBoundsMsg>(OnSetPlaceBounds);
            SetAdminHandler<MoveSpawnPointMsg>(OnMoveSpawnPoint);
            SetAdminHandler<TeleportToPlaceMsg>(OnTeleportToPlace);
            SetAdminHandler<RemoveSpawnPointMsg>(OnRemoveSpawnPoint);
            SetAdminHandler<DeletePlaceMsg>(OnDeletePlace);
            SetAdminHandler<TogglePlaceMsg>(OnTogglePlace);
            SetAdminHandler<RequestTemplateListMsg>(OnRequestTemplateList);
            SetAdminHandler<PlaceTemplateMsg>(OnPlaceTemplate);
            SetAdminHandler<RemovePlaceWithRestoreMsg>(OnRemovePlaceWithRestore);
            SetAdminHandler<TplActionMsg>(OnTplAction);
            SetAdminHandler<RequestAdminEventListMsg>(OnRequestAdminEventList);
            SetAdminHandler<ToggleEventEnabledMsg>(OnToggleEventEnabled);
            SetAdminHandler<AdminActionMsg>(OnAdminAction);
            SetAdminHandler<RequestScheduleStateMsg>(OnRequestScheduleState);
            SetAdminHandler<RequestEventRewardConfigMsg>(OnRequestEventRewardConfig);
            SetAdminHandler<EventRewardConfigMsg>(OnSaveEventRewardConfig);
            SetAdminHandler<RequestRewardLimitsMsg>(OnRequestRewardLimits);
            SetAdminHandler<RewardLimitsMsg>(OnSaveRewardLimits);
            SetAdminHandler<SetCtfPostMsg>(OnSetCtfPost);
            SetAdminHandler<ClearCtfPostsMsg>(OnClearCtfPosts);
            SetAdminHandler<SetHgZoneMsg>(OnSetHgZone);
            SetAdminHandler<AddHgLootMsg>(OnAddHgLoot);
            SetAdminHandler<ClearHgLootMsg>(OnClearHgLoot);
            SetAdminHandler<ListHgLootMsg>(OnListHgLoot);
            SetAdminHandler<RequestHgSetupMsg>(OnRequestHgSetup);
            SetAdminHandler<RemoveHgLootMsg>(OnRemoveHgLoot);
            SetAdminHandler<SetHgLootTierMsg>(OnSetHgLootTier);
            SetAdminHandler<GenerateHgLayoutMsg>(OnGenerateHgLayout);
            SetAdminHandler<HgPreviewMsg>(OnHgPreview);
            SetAdminHandler<SaveHgConfigMsg>(OnSaveHgConfig);
            SetAdminHandler<AddMmPickupMsg>(OnAddMmPickup);
            SetAdminHandler<ClearMmPickupsMsg>(OnClearMmPickups);
            SetAdminHandler<RemoveMmPickupMsg>(OnRemoveMmPickup);
            SetAdminHandler<ListMmPickupsMsg>(OnListMmPickups);
            SetAdminHandler<RequestMmConfigMsg>(OnRequestMmConfig);
            SetAdminHandler<SaveMmConfigMsg>(OnSaveMmConfig);

            // Open to any player: each of these validates against server state instead.
            SetHandler<JoinEventRequestMsg>(OnJoinRequest);
            SetHandler<LeaveEventRequestMsg>(OnLeaveRequest);
            SetHandler<RequestEventListMsg>(OnEventListRequest);
            SetHandler<RequestMatchHistoryMsg>(OnRequestMatchHistory);
            SetHandler<RequestLeaderboardMsg>(OnLeaderboardRequest);
            SetHandler<PartyActionMsg>(OnPartyAction);
            SetHandler<LoadoutPickResponseMsg>(OnLoadoutPickResponse);
            SetHandler<ClearLoadoutDefaultMsg>(OnClearLoadoutDefault);
            SetHandler<RequestLoadoutDefaultsMsg>(OnRequestLoadoutDefaults);
            SetHandler<RequestTournamentListMsg>(OnRequestTournamentList);
            SetHandler<RequestTournamentBracketMsg>(OnRequestTournamentBracket);
            SetHandler<TournamentSignupMsg>(OnTournamentSignup);
            SetHandler<TournamentReadyResponseMsg>(OnTournamentReadyResponse);
            SetAdminHandler<TournamentAdminActionMsg>(OnTournamentAdminAction);

            // Gated on the mod's own spectate privilege rather than controlserver, so a moderator can
            // be given the broadcaster role without the rest of the admin surface.
            RegisterSpectateHandlers();

            // Handing out that privilege, on the other hand, is admin-only.
            RegisterAccessHandlers();

            // Duels need no privilege at all: challenging someone is ordinary play.
            RegisterDuelHandlers();

            // Same for confirming your own queued match.
            RegisterReadyHandlers();
        }

        private void OnPlayerNowPlaying(IServerPlayer player)
        {
            if (_partyManager != null) SendPartyState(player);
            serverChannel.SendPacket(new ClientConfigSyncMsg
            {
                MainDialogHotkey = MainDialogHotkey,
                MaySpectate = MaySpectate(player)
            }, player);
        }

        /// <summary>
        /// Registers a handler that only admins may reach. The privilege check lives at registration
        /// rather than in the handler's first line, so a new handler cannot silently forget it.
        /// Handlers keep their own redundant check as well.
        /// </summary>
        private void SetAdminHandler<T>(NetworkClientMessageHandler<T> handler)
            => SetHandler<T>((player, msg) =>
            {
                if (!RequireAdmin(player)) return;
                handler(player, msg);
            });

        /// <summary>
        /// Registers a packet handler with its errors contained.
        ///
        /// The engine does NOT catch these: <c>ServerNetworkChannel.OnPacket</c> invokes the handler
        /// straight, so anything thrown here unwinds into the server's packet loop. Every handler in
        /// this class reads data a client chose to send, which is exactly where a malformed value or
        /// a stale id is most likely to blow up - and one client's bad packet must not be able to
        /// disturb everyone else's.
        ///
        /// Registration is the place for it rather than each handler's body: a handler added later
        /// gets the guard without its author having to remember.
        /// </summary>
        private void SetHandler<T>(NetworkClientMessageHandler<T> handler)
            => serverChannel.SetMessageHandler<T>((player, msg) =>
            {
                try
                {
                    handler(player, msg);
                }
                catch (Exception e)
                {
                    LogHandlerFailure(typeof(T), player, e);
                }
            });

        // Last time each packet type reported a failure, so a client looping a bad packet cannot turn
        // the log file into the denial of service the try/catch just prevented.
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

        private void Reply(IServerPlayer player, bool success, string message, long placeId = 0)
        {
            serverChannel.SendPacket(
                new PlaceOperationResultMsg { Success = success, Message = message, PlaceId = placeId }, player);
        }

        private void ReplyOk(IServerPlayer player, string message, long placeId = 0)
            => Reply(player, true, message, placeId);

        private void ReplyFail(IServerPlayer player, string message)
            => Reply(player, false, message);

        public void Dispose()
        {
            sapi.Event.PlayerNowPlaying  -= _onPlayerNowPlaying;
            sapi.Event.PlayerDisconnect  -= _onPlayerDisconnect;

            // Every subscription this handler makes is undone here; anything left attached
            // survives a hot-reload and fires into a dead channel.
            UnwirePartyManager();
            UnhookPlatformEditing();
            StopHgPreviewTick();
            RestoreAllCagePreviews();
        }

    }

}
