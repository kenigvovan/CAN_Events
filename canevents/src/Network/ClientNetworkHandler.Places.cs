using System;
using System.Linq;

namespace canevents.src.Network
{
    // Everything the admin panel does to event places: create, bounds, spawn points, the
    // spleef platform and cage, plus the cached place list they all refresh.
    public partial class ClientNetworkHandler
    {
        public readonly ServerState<PlaceListMsg> PlaceList = new ServerState<PlaceListMsg>();
        public PlaceOperationResultMsg LastPlaceResult { get; private set; }
        public event Action<PlaceOperationResultMsg> OnPlaceOperationResult;

        private void OnPlaceList(PlaceListMsg msg)
        {
            // Mirror of the server-side send log: disagreeing counts mean loss on the wire,
            // matching ones mean the panel isn't showing what it has.
            int withBounds = msg?.Places?.Count(p => p.HasBounds) ?? -1;
            capi.Logger.VerboseDebug(string.Format(
                "[canevents] PlaceList<-server: got {0} places, {1} with bounds: {2}",
                msg?.Places?.Length ?? -1, withBounds,
                msg?.Places == null ? "(null)" : string.Join(", ", msg.Places.Where(p => p.HasBounds)
                    .Select(p => p.Name + "#" + p.Id + "(" + p.BoundsMinX + "," + p.BoundsMinY + "," + p.BoundsMinZ + ")"))));

            PlaceList.Set(msg);
        }

        private void OnPlaceResult(PlaceOperationResultMsg msg)
        {
            LastPlaceResult = msg;
            OnPlaceOperationResult?.Invoke(msg);
        }

        public void RequestPlaceList()
        {
            clientChannel.SendPacket(new RequestPlaceListMsg());
        }

        public void SendCreatePlace(string name, string[] types, bool noAutoSpawns = false)
        {
            clientChannel.SendPacket(new CreatePlaceMsg { Name = name, Types = types, NoAutoSpawns = noAutoSpawns });
        }

        public void SendCreatePlaceWithBounds(string name, string[] types,
            int minX, int minY, int minZ, int maxX, int maxY, int maxZ, bool noAutoSpawns = false)
        {
            clientChannel.SendPacket(new CreatePlaceMsg
            {
                Name = name,
                Types = types,
                UseBounds = true,
                BoundsMinX = minX, BoundsMinY = minY, BoundsMinZ = minZ,
                BoundsMaxX = maxX, BoundsMaxY = maxY, BoundsMaxZ = maxZ,
                NoAutoSpawns = noAutoSpawns
            });
        }

        public void SendAddSpawnPoint(long placeId)
        {
            clientChannel.SendPacket(new AddSpawnPointMsg { PlaceId = placeId });
        }

        public void SendMoveSpawnPoint(long placeId, int index)
        {
            clientChannel.SendPacket(new MoveSpawnPointMsg { PlaceId = placeId, Index = index });
        }

        public void SendSetPlaceBounds(long placeId, int minX, int minY, int minZ,
            int maxX, int maxY, int maxZ, bool generateSpawns)
        {
            clientChannel.SendPacket(new SetPlaceBoundsMsg
            {
                PlaceId = placeId,
                BoundsMinX = minX, BoundsMinY = minY, BoundsMinZ = minZ,
                BoundsMaxX = maxX, BoundsMaxY = maxY, BoundsMaxZ = maxZ,
                GenerateSpawns = generateSpawns
            });
        }

        public void SendClearPlaceBounds(long placeId)
        {
            clientChannel.SendPacket(new SetPlaceBoundsMsg { PlaceId = placeId, Clear = true });
        }

        public void SendRemoveSpawnPoint(long placeId, int spawnIndex)
        {
            clientChannel.SendPacket(new RemoveSpawnPointMsg { PlaceId = placeId, SpawnIndex = spawnIndex });
        }

        public void SendDeletePlace(long placeId)
        {
            clientChannel.SendPacket(new DeletePlaceMsg { PlaceId = placeId });
        }

        public void SendRemovePlaceWithRestore(long placeId)
        {
            clientChannel.SendPacket(new RemovePlaceWithRestoreMsg { PlaceId = placeId });
        }

        public void SendTogglePlace(long placeId)
        {
            clientChannel.SendPacket(new TogglePlaceMsg { PlaceId = placeId });
        }

        public void SendTeleportToPlace(long placeId)
        {
            clientChannel.SendPacket(new TeleportToPlaceMsg { PlaceId = placeId });
        }

        // Spleef platform and cage

        public void SendSetCagePos(long placeId, int width, int depth, int height, string block)
        {
            clientChannel.SendPacket(new SetCagePosMsg
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
            clientChannel.SendPacket(new ClearCagePosMsg { PlaceId = placeId });
        }

        /// <summary>Sets team's CTF flag post at the caller's feet, on this exact place.</summary>
        public void SendSetCtfPost(long placeId, int team)
        {
            clientChannel.SendPacket(new SetCtfPostMsg { PlaceId = placeId, Team = team });
        }

        public void SendClearCtfPosts(long placeId)
        {
            clientChannel.SendPacket(new ClearCtfPostsMsg { PlaceId = placeId });
        }

        /// <summary>Marks a Murder Mystery weapon-pickup spot at the caller's feet, on this exact place.</summary>
        public void SendAddMmPickup(long placeId)
        {
            clientChannel.SendPacket(new AddMmPickupMsg { PlaceId = placeId });
        }

        public void SendClearMmPickups(long placeId)
        {
            clientChannel.SendPacket(new ClearMmPickupsMsg { PlaceId = placeId });
        }

        public void SendRemoveMmPickup(long placeId, int index)
        {
            clientChannel.SendPacket(new RemoveMmPickupMsg { PlaceId = placeId, Index = index });
        }

        public void SendListMmPickups(long placeId)
        {
            clientChannel.SendPacket(new ListMmPickupsMsg { PlaceId = placeId });
        }

        /// <summary>Asks for a Murder Mystery event's editable settings; null eventCode lets the
        /// server pick the variant that fits this place.</summary>
        public void SendRequestMmConfig(long placeId, string eventCode = null)
        {
            clientChannel.SendPacket(new RequestMmConfigMsg { PlaceId = placeId, EventCode = eventCode });
        }

        public void SendSaveMmConfig(long placeId, string eventCode, string attributesJson)
        {
            clientChannel.SendPacket(new SaveMmConfigMsg
            {
                PlaceId = placeId,
                EventCode = eventCode,
                AttributesJson = attributesJson
            });
        }

        /// <summary>Sets the HG storm centre at the caller's feet with this radius, on this place.</summary>
        public void SendSetHgZone(long placeId, float radius)
        {
            clientChannel.SendPacket(new SetHgZoneMsg { PlaceId = placeId, Radius = radius });
        }

        public void SendAddHgLoot(long placeId, string tier)
        {
            clientChannel.SendPacket(new AddHgLootMsg { PlaceId = placeId, Tier = tier });
        }

        public void SendClearHgLoot(long placeId)
        {
            clientChannel.SendPacket(new ClearHgLootMsg { PlaceId = placeId });
        }

        public void SendListHgLoot(long placeId)
        {
            clientChannel.SendPacket(new ListHgLootMsg { PlaceId = placeId });
        }

        /// <summary>Asks for the full HG setup picture (tiers, overview, current loot spots) of a place.
        /// eventCode picks which HG variant's config comes back; null lets the server choose.</summary>
        public void SendRequestHgSetup(long placeId, string eventCode = null)
        {
            clientChannel.SendPacket(new RequestHgSetupMsg { PlaceId = placeId, EventCode = eventCode });
        }

        public void SendRemoveHgLoot(long placeId, int index)
        {
            clientChannel.SendPacket(new RemoveHgLootMsg { PlaceId = placeId, Index = index });
        }

        public void SendSetHgLootTier(long placeId, int index, string tier)
        {
            clientChannel.SendPacket(new SetHgLootTierMsg { PlaceId = placeId, Index = index, Tier = tier });
        }

        /// <summary>One-click HG arena: ring of spawns + zone + tiered loot from the caller's feet.
        /// Radius &lt;= 0 fits the place bounds.</summary>
        public void SendGenerateHgLayout(long placeId, float radius, bool snapToTerrain)
        {
            clientChannel.SendPacket(new GenerateHgLayoutMsg { PlaceId = placeId, Radius = radius, SnapToTerrain = snapToTerrain });
        }

        /// <summary>Toggles the in-world zone/loot/spawn preview for this place for the calling admin.</summary>
        public void SendHgPreview(long placeId, bool enabled)
        {
            clientChannel.SendPacket(new HgPreviewMsg { PlaceId = placeId, Enabled = enabled });
        }

        /// <summary>Saves edited HG loot tables + zone phases (as JSON) for an event; PlaceId refreshes the panel.</summary>
        public void SendSaveHgConfig(string eventCode, string lootTablesJson, string zonePhasesJson, long placeId,
            bool applyToAllHgEvents = false)
        {
            clientChannel.SendPacket(new SaveHgConfigMsg
            {
                EventCode = eventCode,
                LootTablesJson = lootTablesJson,
                ZonePhasesJson = zonePhasesJson,
                PlaceId = placeId,
                ApplyToAllHgEvents = applyToAllHgEvents
            });
        }

        public void SendPlatformEditStart(long placeId)
        {
            clientChannel.SendPacket(new PlatformEditStartMsg { PlaceId = placeId });
        }

        public void SendPlatformEditStop(long placeId, bool save)
        {
            clientChannel.SendPacket(new PlatformEditStopMsg { PlaceId = placeId, Save = save });
        }

        public void SendPlatformFillShape(long placeId, bool circle, int radius, string blockCode)
        {
            clientChannel.SendPacket(new PlatformFillShapeMsg { PlaceId = placeId, Circle = circle, Radius = radius, BlockCode = blockCode });
        }

        public void SendPlatformFloodFill(long placeId, int maxRadius, string blockCode)
        {
            clientChannel.SendPacket(new PlatformFloodFillMsg { PlaceId = placeId, MaxRadius = maxRadius, BlockCode = blockCode });
        }

        public readonly ServerState<PlatformEditStateMsg> PlatformEditState = new ServerState<PlatformEditStateMsg>();
    }
}
