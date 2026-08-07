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

            // Open to any player: each of these validates against server state instead.
            serverChannel.SetMessageHandler<JoinEventRequestMsg>(OnJoinRequest);
            serverChannel.SetMessageHandler<LeaveEventRequestMsg>(OnLeaveRequest);
            serverChannel.SetMessageHandler<RequestEventListMsg>(OnEventListRequest);
            serverChannel.SetMessageHandler<RequestMatchHistoryMsg>(OnRequestMatchHistory);
            serverChannel.SetMessageHandler<RequestLeaderboardMsg>(OnLeaderboardRequest);
            serverChannel.SetMessageHandler<PartyActionMsg>(OnPartyAction);
            serverChannel.SetMessageHandler<LoadoutPickResponseMsg>(OnLoadoutPickResponse);
            serverChannel.SetMessageHandler<ClearLoadoutDefaultMsg>(OnClearLoadoutDefault);
            serverChannel.SetMessageHandler<RequestLoadoutDefaultsMsg>(OnRequestLoadoutDefaults);
            serverChannel.SetMessageHandler<RequestTournamentListMsg>(OnRequestTournamentList);
            serverChannel.SetMessageHandler<RequestTournamentBracketMsg>(OnRequestTournamentBracket);
            serverChannel.SetMessageHandler<TournamentSignupMsg>(OnTournamentSignup);
            serverChannel.SetMessageHandler<TournamentReadyResponseMsg>(OnTournamentReadyResponse);
            SetAdminHandler<TournamentAdminActionMsg>(OnTournamentAdminAction);
        }

        private void OnPlayerNowPlaying(IServerPlayer player)
        {
            if (_partyManager != null) SendPartyState(player);
            serverChannel.SendPacket(new ClientConfigSyncMsg { MainDialogHotkey = MainDialogHotkey }, player);
        }

        /// <summary>
        /// Registers a handler that only admins may reach. The privilege check lives at registration
        /// rather than in the handler's first line, so a new handler cannot silently forget it.
        /// Handlers keep their own redundant check as well.
        /// </summary>
        private void SetAdminHandler<T>(NetworkClientMessageHandler<T> handler)
            => serverChannel.SetMessageHandler<T>((player, msg) =>
            {
                if (!RequireAdmin(player)) return;
                handler(player, msg);
            });

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
