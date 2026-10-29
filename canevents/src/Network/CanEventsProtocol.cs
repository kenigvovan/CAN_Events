using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;

namespace canevents.src.Network
{
    public enum ProtocolChannel
    {
        Main = 0,

        // Client-to-server types here must be admin-only; the server enforces it.
        Admin = 1
    }

    // List position = message id. Both sides register from these lists, so keep one copy only.
    // Client channels hold at most 128 types (NetworkChannel.handlers), checked in the static ctor.
    // Nested-only types (*EntryMsg, RewardTierMsg...) are not registered.
    public static class CanEventsProtocol
    {
        // Bump on any change to the lists or to message fields. 1 = pre-1.2.0, no handshake.
        public const int Version = 10;

        public const string MainChannelName = "canevents";
        public const string AdminChannelName = "caneventsadmin";

        // VS NetworkChannel.handlers size on the client.
        public const int ClientHandlerSlots = 128;

        // ProtocolHandshakeMsg must stay at id 0 so different versions can read it.
        private static readonly Type[] MainTypes =
        {
            typeof(ProtocolHandshakeMsg),
            typeof(ClientConfigSyncMsg),
            typeof(PlaceOperationResultMsg),

            // Match HUD
            typeof(EventStateUpdateMsg),
            typeof(ScoreboardUpdateMsg),
            typeof(KillFeedMsg),
            typeof(EventResultMsg),
            typeof(CtfCarriersMsg),
            typeof(HgZoneStateMsg),

            // Events browser, queue and history
            typeof(RequestEventListMsg),
            typeof(EventListMsg),
            typeof(JoinEventRequestMsg),
            typeof(LeaveEventRequestMsg),
            typeof(EventInviteMsg),
            typeof(EventInviteClosedMsg),
            typeof(MatchReadyRequestMsg),
            typeof(MatchReadyResponseMsg),
            typeof(MatchReadyClosedMsg),
            typeof(RequestLeaderboardMsg),
            typeof(LeaderboardMsg),
            typeof(RequestMatchHistoryMsg),
            typeof(MatchHistoryMsg),

            // Party
            typeof(PartyActionMsg),
            typeof(PartyStateMsg),
            typeof(PartyOperationResultMsg),

            // Loadout picks and defaults
            typeof(LoadoutPickRequestMsg),
            typeof(LoadoutPickResponseMsg),
            typeof(ClearLoadoutDefaultMsg),
            typeof(RequestLoadoutDefaultsMsg),
            typeof(LoadoutDefaultsListMsg),

            // Tournaments, player side
            typeof(RequestTournamentListMsg),
            typeof(TournamentListMsg),
            typeof(RequestTournamentBracketMsg),
            typeof(TournamentBracketMsg),
            typeof(TournamentSignupMsg),
            typeof(TournamentRenameMsg),
            typeof(TournamentLoadoutMsg),
            typeof(TournamentReadyRequestMsg),
            typeof(TournamentReadyResponseMsg),
            typeof(TournamentReadyClosedMsg),

            // Spectating (gated on the spectate privilege, not on admin)
            typeof(SpectateRequestMsg),
            typeof(SpectateStateMsg),
            typeof(SpectateFollowMsg),
            typeof(SpectatorTelemetryMsg),
            typeof(SpectateCameraMsg),

            // Duels
            typeof(DuelChallengeMsg),
            typeof(DuelPromptMsg),
            typeof(DuelResponseMsg),
            typeof(DuelClosedMsg),
            typeof(RequestDuelTargetsMsg),
            typeof(DuelTargetListMsg),

            // Waiting room side pick (protocol v6)
            typeof(TeamPickStateMsg),
            typeof(TeamPickResponseMsg),

            // Team pennants (protocol v8)
            typeof(TeamMarkersMsg),

            // Match shop (protocol v10)
            typeof(ShopStateMsg),
            typeof(ShopRequestMsg),
            typeof(ShopActionMsg),
            typeof(ShopPreviewRequestMsg),
            typeof(ShopPreviewMsg),
        };

        // PlaceOperationResultMsg is on both channels (tournament signup answers on main).
        private static readonly Type[] AdminTypes =
        {
            typeof(PlaceOperationResultMsg),

            // Places
            typeof(RequestPlaceListMsg),
            typeof(PlaceListMsg),
            typeof(CreatePlaceMsg),
            typeof(SetPlaceBoundsMsg),
            typeof(AddSpawnPointMsg),
            typeof(MoveSpawnPointMsg),
            typeof(RemoveSpawnPointMsg),
            typeof(TeleportToPlaceMsg),
            typeof(TeleportToHgLootSpotMsg),
            typeof(TogglePlaceMsg),
            typeof(BulkEditPlacesMsg),
            typeof(DeletePlacesMsg),
            typeof(EditPlaceInfoMsg),

            // Templates and schematics
            typeof(RequestTemplateListMsg),
            typeof(TemplateListMsg),
            typeof(PlaceTemplateMsg),
            typeof(RemovePlaceWithRestoreMsg),
            typeof(TplActionMsg),
            typeof(TplSessionStateMsg),
            typeof(RequestSchematicListMsg),
            typeof(SchematicListMsg),
            typeof(CreateTemplateFromSchematicMsg),

            // Spleef
            typeof(SetCagePosMsg),
            typeof(ClearCagePosMsg),
            typeof(PlatformEditStartMsg),
            typeof(PlatformEditStopMsg),
            typeof(PlatformFillShapeMsg),
            typeof(PlatformFloodFillMsg),
            typeof(PlatformEditStateMsg),

            // Capture the Flag
            typeof(SetCtfPostMsg),
            typeof(ClearCtfPostsMsg),

            // Parkour and King of the Hill
            typeof(SetParkourCheckpointMsg),
            typeof(RemoveParkourCheckpointMsg),
            typeof(SetParkourCheckpointIndexMsg),
            typeof(SetParkourLivesMsg),
            typeof(SetKothHillMsg),

            // Hunger Games
            typeof(SetHgZoneMsg),
            typeof(AddHgLootMsg),
            typeof(ClearHgLootMsg),
            typeof(RemoveHgLootMsg),
            typeof(SetHgLootTierMsg),
            typeof(RequestHgSetupMsg),
            typeof(HgSetupMsg),
            typeof(GenerateHgLayoutMsg),
            typeof(HgPreviewMsg),
            typeof(SaveHgConfigMsg),

            // Murder Mystery
            typeof(AddMmPickupMsg),
            typeof(ClearMmPickupsMsg),
            typeof(RemoveMmPickupMsg),
            typeof(RequestMmConfigMsg),
            typeof(MmConfigMsg),
            typeof(SaveMmConfigMsg),

            // Item sets
            typeof(RequestLoadoutListMsg),
            typeof(LoadoutListMsg),
            typeof(SaveLoadoutMsg),
            typeof(DeleteLoadoutSetMsg),
            typeof(ImportInlineSetsMsg),

            // Events, scheduler and rewards
            typeof(RequestAdminEventListMsg),
            typeof(AdminEventListMsg),
            typeof(ToggleEventEnabledMsg),
            typeof(AdminActionMsg),
            typeof(RequestScheduleStateMsg),
            typeof(ScheduleStateMsg),
            typeof(RequestEventRewardConfigMsg),
            typeof(EventRewardConfigMsg),
            typeof(RequestRewardLimitsMsg),
            typeof(RewardLimitsMsg),

            // Tournaments, admin side
            typeof(TournamentAdminActionMsg),

            // Player access
            typeof(RequestPlayerAccessMsg),
            typeof(PlayerAccessListMsg),
            typeof(PlayerAccessActionMsg),

            // Inventory audit (was the separate "caneventsinvaudit" channel)
            typeof(OpenInventoryAuditMsg),
            typeof(RequestInventoryAuditMsg),
            typeof(InventoryAuditListMsg),
            typeof(RequestInventoryBackupDetailMsg),
            typeof(InventoryBackupDetailMsg),
            typeof(InventoryBackupActionMsg),

            // Server config editor (protocol v5)
            typeof(RequestServerSettingsMsg),
            typeof(ServerSettingsMsg),
            typeof(SetServerSettingMsg),

            // Waiting room (protocol v6)
            typeof(SetWaitingRoomMsg),
            typeof(PadEditMsg),
            typeof(PadListMsg),

            // Event behavior settings editor (protocol v7)
            typeof(RequestEventSettingsMsg),
            typeof(EventSettingsMsg),
            typeof(SetEventSettingMsg),

            // Match shop editor (protocol v10)
            typeof(RequestShopConfigMsg),
            typeof(ShopConfigMsg),
            typeof(ShopEnableMsg),
        };

        // Order in which both sides open them.
        public static readonly IReadOnlyList<ProtocolChannel> Channels =
            new[] { ProtocolChannel.Main, ProtocolChannel.Admin };

        private static readonly Dictionary<Type, ProtocolChannel[]> channelsByType;

        static CanEventsProtocol()
        {
            var index = new Dictionary<Type, List<ProtocolChannel>>();
            foreach (var channel in Channels)
            {
                var types = TypesOn(channel);

                // The engine's own error past 128 is an unhelpful IndexOutOfRangeException.
                if (types.Count > ClientHandlerSlots)
                    throw new InvalidOperationException(string.Format(
                        "[canevents] Channel '{0}' lists {1} message types; a client channel holds at most {2}. "
                        + "Move some to another channel.", NameOf(channel), types.Count, ClientHandlerSlots));

                var seen = new HashSet<Type>();
                foreach (var type in types)
                {
                    if (!seen.Add(type))
                        throw new InvalidOperationException(string.Format(
                            "[canevents] {0} is listed twice on channel '{1}'.", type.Name, NameOf(channel)));

                    if (!index.TryGetValue(type, out var list)) index[type] = list = new List<ProtocolChannel>();
                    list.Add(channel);
                }
            }
            channelsByType = index.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
        }

        public static string NameOf(ProtocolChannel channel)
            => channel == ProtocolChannel.Admin ? AdminChannelName : MainChannelName;

        public static IReadOnlyList<Type> TypesOn(ProtocolChannel channel)
            => channel == ProtocolChannel.Admin ? AdminTypes : MainTypes;

        // Registration order is the id, both sides must use this.
        public static void Register(INetworkChannel channel, ProtocolChannel which)
        {
            foreach (var type in TypesOn(which)) channel.RegisterMessageType(type);
        }

        // Throws for types on more than one channel; pass the channel explicitly for those.
        public static ProtocolChannel ChannelFor<T>() => ChannelFor(typeof(T));

        public static ProtocolChannel ChannelFor(Type type)
        {
            var channels = ChannelsOf(type);
            if (channels.Count != 1)
                throw new InvalidOperationException(string.Format(
                    "[canevents] {0} is registered on {1} channels; name the channel explicitly.",
                    type.Name, channels.Count));
            return channels[0];
        }

        public static IReadOnlyList<ProtocolChannel> ChannelsOf(Type type)
        {
            if (channelsByType.TryGetValue(type, out var channels)) return channels;
            throw new InvalidOperationException(string.Format(
                "[canevents] {0} is not registered on any channel; add it to CanEventsProtocol.", type.Name));
        }

        public static string Summary()
            => string.Format("[canevents] Network protocol v{0}: {1} (client limit {2} per channel)",
                Version,
                string.Join(", ", Channels.Select(c => "'" + NameOf(c) + "' " + TypesOn(c).Count + " types")),
                ClientHandlerSlots);
    }
}
