using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventPlacesManagers.Places;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace canevents.src.Network
{
    // Place handlers. The actual edits live in PlaceConfigService.
    public partial class ServerNetworkHandler
    {
        // Fails silently, a non-admin client shouldn't send these at all.
        private static bool RequireAdmin(IServerPlayer player)
            => player.HasPrivilege(Privilege.controlserver);

        // Replies "not found" itself.
        private bool TryResolvePlace(IServerPlayer player, long placeId, out EventPlace place)
        {
            place = eventManager.eventPlacesManager.FindById(placeId);
            if (place != null) return true;
            ReplyFail(player, Lang.Get("canevents:place-not-found-id", placeId));
            return false;
        }

        // Same methods the /canep commands use.
        private PlaceConfigService PlaceConfig => eventManager.placeConfig;

        // Block under the admin's feet.
        private static Vintagestory.API.MathTools.BlockPos FeetBlock(IServerPlayer player)
            => player.Entity.Pos.AsBlockPos.DownCopy();

        private bool ApplyPlaceEdit(IServerPlayer player, EventPlace place, PlaceEditResult result)
        {
            Reply(player, result.Ok, result.Message, place.Id);
            return result.Ok;
        }

        // refreshOnFailure is for index-based edits, where a rejection means the panel list is stale.
        // System.Func because Vintagestory.API.Common has its own Func<>.
        private void PlaceEdit(IServerPlayer player, long placeId, System.Func<EventPlace, PlaceEditResult> edit,
            Action<EventPlace> afterEdit = null, bool refreshOnFailure = false)
        {
            if (!RequireAdmin(player)) return;
            if (!TryResolvePlace(player, placeId, out var place)) return;

            if (!ApplyPlaceEdit(player, place, edit(place)) && !refreshOnFailure) return;

            afterEdit?.Invoke(place);
            SendPlaceListTo(player);
        }

        // dim is the admin's current dimension; the message has none.
        private static (Vintagestory.API.MathTools.BlockPos a, Vintagestory.API.MathTools.BlockPos b)
            BoundsCorners(int x1, int y1, int z1, int x2, int y2, int z2, int dim)
            => (new Vintagestory.API.MathTools.BlockPos(x1, y1, z1, dim),
                new Vintagestory.API.MathTools.BlockPos(x2, y2, z2, dim));

        private void SendPlaceListTo(IServerPlayer player) => OnPlaceListRequest(player, null);

        private void OnPlaceListRequest(IServerPlayer player, RequestPlaceListMsg msg)
        {
            if (!RequireAdmin(player)) return;

            var placeManager = eventManager.eventPlacesManager;
            var entries = new List<PlaceListEntryMsg>();

            foreach (var place in placeManager.EventPlaces)
            {
                var spawns = place.spawnPositions;
                var mmPickups = PlaceSummaryFormatter.PackMurderMysteryPickups(place);
                entries.Add(new PlaceListEntryMsg
                {
                    Id = place.Id,
                    Name = place.Name ?? "",
                    Types = place.placeTypes?.Select(t => t.ToString()).ToArray() ?? Array.Empty<string>(),
                    SpawnCount = spawns?.Count ?? 0,
                    Occupied = place.Occupied,
                    Enabled = place.Enabled,
                    SpawnX = spawns?.Select(p => p.X).ToArray() ?? Array.Empty<int>(),
                    SpawnY = spawns?.Select(p => p.Y).ToArray() ?? Array.Empty<int>(),
                    SpawnZ = spawns?.Select(p => p.Z).ToArray() ?? Array.Empty<int>(),
                    HasBounds = place.HasBounds,
                    BoundsMinX = place.BoundsMin?.X ?? 0,
                    BoundsMinY = place.BoundsMin?.Y ?? 0,
                    BoundsMinZ = place.BoundsMin?.Z ?? 0,
                    BoundsMaxX = place.BoundsMax?.X ?? 0,
                    BoundsMaxY = place.BoundsMax?.Y ?? 0,
                    BoundsMaxZ = place.BoundsMax?.Z ?? 0,
                    KothHill = PlaceSummaryFormatter.PackKothHill(place),
                    Description = place.Description,
                    SchematicTemplateCode = place.SchematicTemplateCode,
                    SchematicOnDemand = place.SchematicOnDemand,
                    BehaviorSummary = PlaceSummaryFormatter.Summarize(place),
                    MmPickupX = mmPickups.Select(p => p.X).ToArray(),
                    MmPickupY = mmPickups.Select(p => p.Y).ToArray(),
                    MmPickupZ = mmPickups.Select(p => p.Z).ToArray(),
                    ParkourCheckpoints = PlaceSummaryFormatter.PackParkourCheckpoints(place),
                    ParkourLives = PlaceSummaryFormatter.ParkourLives(place),
                    SetupGaps = PlaceSetupCheck.Gaps(place).ToArray(),
                    Dim = PlaceSummaryFormatter.Dimension(place),
                    HgZoneSet = PlaceSummaryFormatter.HasHgZone(place),
                    SpleefPlatformBlocks = PlaceSummaryFormatter.SpleefPlatformBlocks(place),
                    CtfPosts = PlaceSummaryFormatter.PackCtfPosts(place),
                    WaitingRoom = PlaceConfigService.TryGetWaitingRoom(place, out var room)
                        ? room.X + "," + room.Y + "," + room.Z
                        : null
                });
            }

            // Compare with the client-side receive log.
            sapi.World.Logger.VerboseDebug(string.Format(
                "[canevents] PlaceList->{0}: sending {1} places, {2} with bounds: {3}",
                player.PlayerName, entries.Count, entries.Count(e => e.HasBounds),
                string.Join(", ", entries.Where(e => e.HasBounds)
                    .Select(e => e.Name + "#" + e.Id + "(" + e.BoundsMinX + "," + e.BoundsMinY + "," + e.BoundsMinZ + ")"))));

            adminChannel.SendPacket(new PlaceListMsg { Places = entries.ToArray() }, player);
        }

        private void OnCreatePlace(IServerPlayer player, CreatePlaceMsg msg)
        {
            if (!RequireAdmin(player)) return;

            var placeManager = eventManager.eventPlacesManager;

            var types = new List<string>();
            if (msg.Types != null)
            {
                foreach (var typeName in msg.Types)
                {
                    var tag = PlaceTags.Normalize(typeName);
                    if (!string.IsNullOrEmpty(tag)) types.Add(tag);
                }
            }

            var place = new EventPlace
            {
                Name = PlaceConfigService.CleanText(msg.Name, PlaceConfigService.MaxNameLength) ?? "Unnamed",
                placeTypes = types.ToArray()
            };

            if (!msg.NoAutoSpawns) place.spawnPositions.Add(player.Entity.Pos.AsBlockPos);

            if (placeManager.AddPlace(place))
            {
                // Wizard needs the id.
                ReplyOk(player, Lang.Get("canevents:place-created-id", place.Name, place.Id), place.Id);
                SendPlaceListTo(player);
            }
            else
            {
                ReplyFail(player, Lang.Get("canevents:place-create-failed"));
            }
        }

        // First spawn, else bounds centre, else KOTH hill.
        private void OnTeleportToPlace(IServerPlayer player, TeleportToPlaceMsg msg)
        {
            if (!RequireAdmin(player)) return;
            if (!TryResolvePlace(player, msg.PlaceId, out var place)) return;

            if (msg.SpawnNumber > 0)
            {
                var spawns = place.spawnPositions;
                if (spawns == null || msg.SpawnNumber > spawns.Count)
                {
                    ReplyFail(player, Lang.Get("canevents:place-no-spawn", place.Name, msg.SpawnNumber));
                    return;
                }
                var sp = spawns[msg.SpawnNumber - 1];
                EventTeleport.Send(sapi, player, sp.X + 0.5, sp.Y, sp.Z + 0.5, sp.dimension,
                    "admin go-to '" + place.Name + "' spawn #" + msg.SpawnNumber);
                ReplyOk(player, Lang.Get("canevents:place-teleported", place.Name, "spawn #" + msg.SpawnNumber), place.Id);
                return;
            }

            if (!PlaceConfigService.TryFindTeleportPoint(place, out double x, out double y, out double z,
                    out int dim, out string what))
            {
                ReplyFail(player, Lang.Get("canevents:place-nowhere-to-tp", place.Name));
                return;
            }

            EventTeleport.Send(sapi, player, x, y, z, dim, "admin go-to '" + place.Name + "'");
            ReplyOk(player, Lang.Get("canevents:place-teleported", place.Name, what), place.Id);
        }

        private void OnSetPlaceBounds(IServerPlayer player, SetPlaceBoundsMsg msg)
            => PlaceEdit(player, msg.PlaceId, p =>
            {
                if (msg.Clear) return PlaceConfig.ClearBounds(p);
                var (cornerA, cornerB) = BoundsCorners(msg.BoundsMinX, msg.BoundsMinY, msg.BoundsMinZ,
                                                       msg.BoundsMaxX, msg.BoundsMaxY, msg.BoundsMaxZ,
                                                       player.Entity.Pos.Dimension);
                // Block accessor so generated spawns land on the floor.
                return PlaceConfig.SetBounds(p, cornerA, cornerB, msg.GenerateSpawns, sapi.World.BlockAccessor);
            });

        private void OnAddSpawnPoint(IServerPlayer player, AddSpawnPointMsg msg)
            => PlaceEdit(player, msg.PlaceId, p => PlaceConfig.AddSpawn(p, player.Entity.Pos.AsBlockPos));

        private void OnMoveSpawnPoint(IServerPlayer player, MoveSpawnPointMsg msg)
            => PlaceEdit(player, msg.PlaceId, p => PlaceConfig.MoveSpawn(p, msg.Index, player.Entity.Pos.AsBlockPos),
                refreshOnFailure: true);

        private void OnRemoveSpawnPoint(IServerPlayer player, RemoveSpawnPointMsg msg)
            => PlaceEdit(player, msg.PlaceId, p => PlaceConfig.RemoveSpawn(p, msg.SpawnIndex),
                refreshOnFailure: true);

        private void OnDeletePlaces(IServerPlayer player, DeletePlacesMsg msg)
        {
            if (!RequireAdmin(player)) return;
            if (msg?.PlaceIds == null || msg.PlaceIds.Length == 0) return;

            var manager = eventManager.eventPlacesManager;
            int deleted = 0;
            var busy = new List<string>();
            // Extra lines of each result, e.g. tournament arena list changes.
            var followUps = new List<string>();
            foreach (long id in msg.PlaceIds.Distinct())
            {
                var p = manager.FindById(id);
                if (p == null) continue;
                var result = PlaceConfig.DeletePlace(p);
                if (result.Ok)
                {
                    deleted++;
                    followUps.AddRange(result.Message.Split('\n').Skip(1));
                }
                else busy.Add(p.Name + " #" + id);
            }
            ReplyOk(player, Lang.Get("canevents:places-bulk-deleted", deleted) + SkippedBusySuffix(busy)
                + (followUps.Count > 0 ? "\n" + string.Join("\n", followUps) : ""));
            SendPlaceListTo(player);
        }

        private void OnEditPlaceInfo(IServerPlayer player, EditPlaceInfoMsg msg)
            => PlaceEdit(player, msg.PlaceId, p => PlaceConfig.UpdateInfo(p, msg.Name, msg.Description, msg.Tags));

        private void OnTogglePlace(IServerPlayer player, TogglePlaceMsg msg)
            => PlaceEdit(player, msg.PlaceId, p => PlaceConfig.SetEnabled(p, !p.Enabled));

        // Places hosting a match are skipped.
        private void OnBulkEditPlaces(IServerPlayer player, BulkEditPlacesMsg msg)
        {
            if (!RequireAdmin(player)) return;
            if (msg?.PlaceIds == null || msg.PlaceIds.Length == 0) return;

            var manager = eventManager.eventPlacesManager;
            bool? enabled = msg.EnabledMode == BulkEnabledMode.Enable ? true
                          : msg.EnabledMode == BulkEnabledMode.Disable ? false
                          : (bool?)null;
            int changed = 0;
            var busy = new List<string>();

            foreach (long id in msg.PlaceIds.Distinct())
            {
                var p = manager.FindById(id);
                if (p == null) continue;
                // Only fails when the place is hosting a match.
                if (!PlaceConfig.ApplyBulk(p, enabled, msg.AddType, msg.RemoveType, out bool placeChanged).Ok)
                {
                    busy.Add(p.Name + " #" + id);
                    continue;
                }
                if (placeChanged) changed++;
            }

            ReplyOk(player, Lang.Get("canevents:places-bulk-updated", changed, msg.PlaceIds.Length) + SkippedBusySuffix(busy));
            SendPlaceListTo(player);
        }

        private static string SkippedBusySuffix(List<string> busy)
            => busy.Count > 0 ? " " + Lang.Get("canevents:places-bulk-skipped-busy", string.Join(", ", busy)) : "";
    }
}
