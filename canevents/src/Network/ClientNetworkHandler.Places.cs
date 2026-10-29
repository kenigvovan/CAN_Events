using System;
using System.Linq;

namespace canevents.src.Network
{
    public partial class ClientNetworkHandler
    {
        public readonly ServerState<PlaceListMsg> PlaceList = new ServerState<PlaceListMsg>();
        // Floor zones (join/leave/side); resent after every zone edit.
        public readonly ServerState<PadListMsg> PadList = new ServerState<PadListMsg>();
        public event Action<PlaceOperationResultMsg> OnPlaceOperationResult;

        private void OnPlaceList(PlaceListMsg msg)
        {
            // Compare with the server-side send log.
            int withBounds = msg?.Places?.Count(p => p.HasBounds) ?? -1;
            capi.Logger.VerboseDebug(string.Format(
                "[canevents] PlaceList<-server: got {0} places, {1} with bounds: {2}",
                msg?.Places?.Length ?? -1, withBounds,
                msg?.Places == null ? "(null)" : string.Join(", ", msg.Places.Where(p => p.HasBounds)
                    .Select(p => p.Name + "#" + p.Id + "(" + p.BoundsMinX + "," + p.BoundsMinY + "," + p.BoundsMinZ + ")"))));

            PlaceList.Set(msg);
        }

        private void OnPlaceResult(PlaceOperationResultMsg msg) => OnPlaceOperationResult?.Invoke(msg);

        public void RequestPlaceList()
        {
            Send(new RequestPlaceListMsg());
        }

        public void SendCreatePlace(string name, string[] types, bool noAutoSpawns = false)
        {
            Send(new CreatePlaceMsg { Name = name, Types = types, NoAutoSpawns = noAutoSpawns });
        }

        public void SendAddSpawnPoint(long placeId)
        {
            Send(new AddSpawnPointMsg { PlaceId = placeId });
        }

        public void SendMoveSpawnPoint(long placeId, int index)
        {
            Send(new MoveSpawnPointMsg { PlaceId = placeId, Index = index });
        }

        public void SendSetPlaceBounds(long placeId, int minX, int minY, int minZ,
            int maxX, int maxY, int maxZ, bool generateSpawns)
        {
            Send(new SetPlaceBoundsMsg
            {
                PlaceId = placeId,
                BoundsMinX = minX, BoundsMinY = minY, BoundsMinZ = minZ,
                BoundsMaxX = maxX, BoundsMaxY = maxY, BoundsMaxZ = maxZ,
                GenerateSpawns = generateSpawns
            });
        }

        public void SendClearPlaceBounds(long placeId)
        {
            Send(new SetPlaceBoundsMsg { PlaceId = placeId, Clear = true });
        }

        public void SendRemoveSpawnPoint(long placeId, int spawnIndex)
        {
            Send(new RemoveSpawnPointMsg { PlaceId = placeId, SpawnIndex = spawnIndex });
        }

        public void SendRemovePlaceWithRestore(long placeId)
        {
            Send(new RemovePlaceWithRestoreMsg { PlaceId = placeId });
        }

        public void SendTogglePlace(long placeId)
        {
            Send(new TogglePlaceMsg { PlaceId = placeId });
        }

        public void SendEditPlaceInfo(long placeId, string name, string description, string[] tags)
        {
            Send(new EditPlaceInfoMsg { PlaceId = placeId, Name = name, Description = description, Tags = tags });
        }

        public void SendBulkEditPlaces(long[] placeIds, BulkEnabledMode enabledMode,
            string addType = null, string removeType = null)
        {
            Send(new BulkEditPlacesMsg
            {
                PlaceIds = placeIds, EnabledMode = enabledMode, AddType = addType, RemoveType = removeType
            });
        }

        public void SendBulkDeletePlaces(long[] placeIds)
        {
            Send(new DeletePlacesMsg { PlaceIds = placeIds });
        }

        public void SendTeleportToPlace(long placeId)
        {
            Send(new TeleportToPlaceMsg { PlaceId = placeId });
        }

        public void SendTeleportToSpawn(long placeId, int spawnIndex)
        {
            Send(new TeleportToPlaceMsg { PlaceId = placeId, SpawnNumber = spawnIndex + 1 });
        }

        public void SendTeleportToHgLoot(long placeId, int spotIndex)
        {
            Send(new TeleportToHgLootSpotMsg { PlaceId = placeId, SpotIndex = spotIndex });
        }

        public void SendSetParkourCheckpoint(long placeId, bool isFinish)
        {
            Send(new SetParkourCheckpointMsg { PlaceId = placeId, IsFinish = isFinish });
        }

        public void SendRemoveParkourCheckpoint(long placeId, int x, int y, int z, int dim)
        {
            Send(new RemoveParkourCheckpointMsg { PlaceId = placeId, X = x, Y = y, Z = z, Dim = dim });
        }

        public void SendSetParkourCheckpointIndex(long placeId, int x, int y, int z, int dim, int index)
        {
            Send(new SetParkourCheckpointIndexMsg { PlaceId = placeId, X = x, Y = y, Z = z, Dim = dim, Index = index });
        }

        public void SendSetParkourLives(long placeId, int lives)
        {
            Send(new SetParkourLivesMsg { PlaceId = placeId, Lives = lives });
        }

        // height <= 0 = default.
        public void SendSetKothHill(long placeId, float radius, float height)
        {
            Send(new SetKothHillMsg { PlaceId = placeId, Radius = radius, Height = height });
        }

        public void SendSetWaitingRoom(long placeId)
        {
            Send(new SetWaitingRoomMsg { PlaceId = placeId });
        }

        // op: see PadEditMsg. padId only for the list's row actions.
        public void SendPadEdit(string op, string eventCode = null, int team = 0, int padId = 0)
        {
            Send(new PadEditMsg { Op = op, EventCode = eventCode, Team = team, PadId = padId });
        }

        // side/reshape with the box marked in the panel (PlaceHighlighter marks).
        public void SendPadBox(string op, string eventCode, int team, long placeId,
            int x1, int y1, int z1, int x2, int y2, int z2)
        {
            Send(new PadEditMsg
            {
                Op = op, EventCode = eventCode, Team = team, PlaceId = placeId,
                HasBox = true, X = x1, Y = y1, Z = z1, X2 = x2, Y2 = y2, Z2 = z2
            });
        }

        public void SendSetCagePos(long placeId, int width, int depth, int height, string block)
        {
            Send(new SetCagePosMsg
            {
                PlaceId = placeId,
                Width   = width,
                Depth   = depth,
                Height  = height,
                Block   = block
            });
        }

        public void SendClearCagePos(long placeId)
        {
            Send(new ClearCagePosMsg { PlaceId = placeId });
        }

        public void SendSetCtfPost(long placeId, int team)
        {
            Send(new SetCtfPostMsg { PlaceId = placeId, Team = team });
        }

        public void SendClearCtfPosts(long placeId)
        {
            Send(new ClearCtfPostsMsg { PlaceId = placeId });
        }

        public void SendAddMmPickup(long placeId)
        {
            Send(new AddMmPickupMsg { PlaceId = placeId });
        }

        public void SendClearMmPickups(long placeId)
        {
            Send(new ClearMmPickupsMsg { PlaceId = placeId });
        }

        public void SendRemoveMmPickup(long placeId, int index)
        {
            Send(new RemoveMmPickupMsg { PlaceId = placeId, Index = index });
        }

        // null eventCode = server picks the variant.
        public void SendRequestMmConfig(long placeId, string eventCode = null)
        {
            Send(new RequestMmConfigMsg { PlaceId = placeId, EventCode = eventCode });
        }

        public void SendSaveMmConfig(long placeId, string eventCode, string attributesJson)
        {
            Send(new SaveMmConfigMsg
            {
                PlaceId = placeId,
                EventCode = eventCode,
                AttributesJson = attributesJson
            });
        }

        public void SendSetHgZone(long placeId, float radius, HgZoneMode mode = HgZoneMode.AtFeet)
        {
            Send(new SetHgZoneMsg { PlaceId = placeId, Radius = radius, Mode = mode });
        }

        public void SendAddHgLoot(long placeId, string tier)
        {
            Send(new AddHgLootMsg { PlaceId = placeId, Tier = tier });
        }

        public void SendClearHgLoot(long placeId)
        {
            Send(new ClearHgLootMsg { PlaceId = placeId });
        }

        // null eventCode = server picks the variant.
        public void SendRequestHgSetup(long placeId, string eventCode = null)
        {
            Send(new RequestHgSetupMsg { PlaceId = placeId, EventCode = eventCode });
        }

        public void SendRemoveHgLoot(long placeId, int index)
        {
            Send(new RemoveHgLootMsg { PlaceId = placeId, Index = index });
        }

        public void SendSetHgLootTier(long placeId, int index, string tier)
        {
            Send(new SetHgLootTierMsg { PlaceId = placeId, Index = index, Tier = tier });
        }

        // radius <= 0 fits the place bounds.
        public void SendGenerateHgLayout(long placeId, float radius, bool snapToTerrain, int spawnCount)
        {
            Send(new GenerateHgLayoutMsg
            {
                PlaceId = placeId, Radius = radius, SnapToTerrain = snapToTerrain, SpawnCount = spawnCount
            });
        }

        public void SendHgPreview(long placeId, bool enabled)
        {
            Send(new HgPreviewMsg { PlaceId = placeId, Enabled = enabled });
        }

        public void SendSaveHgConfig(string eventCode, string lootTablesJson, string zonePhasesJson, long placeId,
            bool applyToAllHgEvents = false, string scoreboardVisibility = null)
        {
            Send(new SaveHgConfigMsg
            {
                EventCode = eventCode,
                LootTablesJson = lootTablesJson,
                ZonePhasesJson = zonePhasesJson,
                PlaceId = placeId,
                ApplyToAllHgEvents = applyToAllHgEvents,
                ScoreboardVisibility = scoreboardVisibility
            });
        }

        public void SendPlatformEditStart(long placeId)
        {
            Send(new PlatformEditStartMsg { PlaceId = placeId });
        }

        public void SendPlatformEditStop(long placeId, bool save)
        {
            Send(new PlatformEditStopMsg { PlaceId = placeId, Save = save });
        }

        public void SendPlatformFillShape(long placeId, bool circle, int radius, string blockCode)
        {
            Send(new PlatformFillShapeMsg { PlaceId = placeId, Circle = circle, Radius = radius, BlockCode = blockCode });
        }

        public void SendPlatformFloodFill(long placeId, int maxRadius, string blockCode)
        {
            Send(new PlatformFloodFillMsg { PlaceId = placeId, MaxRadius = maxRadius, BlockCode = blockCode });
        }

        public readonly ServerState<PlatformEditStateMsg> PlatformEditState = new ServerState<PlatformEditStateMsg>();
    }
}
