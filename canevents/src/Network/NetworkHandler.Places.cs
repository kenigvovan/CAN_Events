using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventPlacesManagers.Places;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace canevents.src.Network
{
    // Server-side event-place management: list/create/delete places, add/remove spawn points,
    // toggle enabled. The per-behavior setup handlers moved out to the file of the mode they serve
    // (.Ctf, .HungerGames); the prologue helpers they all share stay here.
    public partial class ServerNetworkHandler
    {
        // Handler prologue helpers: the privilege gate, the id lookup and the not-found reply every
        // place handler opens with, kept in one place so they all answer identically.

        /// <summary>Admin gate. Handlers return silently when it fails - a client that isn't admin
        /// shouldn't have sent the packet at all.</summary>
        private static bool RequireAdmin(IServerPlayer player)
            => player.HasPrivilege(Privilege.controlserver);

        /// <summary>Resolves the place by id, answering "not found" itself when it isn't there.</summary>
        private bool TryResolvePlace(IServerPlayer player, long placeId, out EventPlace place)
        {
            place = eventManager.eventPlacesManager.FindById(placeId);
            if (place != null) return true;
            ReplyFail(player, Lang.Get("canevents:place-not-found-id", placeId));
            return false;
        }

        /// <summary>Blocks edits to a place that is hosting a match right now.</summary>
        private bool RequireNotOccupied(IServerPlayer player, EventPlace place)
        {
            if (!place.Occupied) return true;
            ReplyFail(player, Lang.Get("canevents:place-in-use"));
            return false;
        }

        /// <summary>Shared implementation of the per-behavior place edits (HG zone/loot, CTF posts,
        /// KOTH hill); the /canep commands call the very same methods.</summary>
        private PlaceConfigService PlaceConfig => eventManager.placeConfig;

        /// <summary>The block the admin is standing on: what every "set this at my feet" edit writes.</summary>
        private static Vintagestory.API.MathTools.BlockPos FeetBlock(IServerPlayer player)
            => player.Entity.Pos.AsBlockPos.DownCopy();

        /// <summary>Relays a place edit's outcome to the admin; returns whether it went through, so
        /// the caller can skip the follow-up pushes on a rejected edit.</summary>
        private bool ApplyPlaceEdit(IServerPlayer player, EventPlace place, PlaceEditResult result)
        {
            Reply(player, result.Ok, result.Message, place.Id);
            return result.Ok;
        }

        /// <summary>
        /// The whole shape of a per-behavior place edit: admin gate, id lookup, the edit, the reply,
        /// the mode's own panel refresh (<paramref name="afterEdit"/>) and the place list.
        /// <paramref name="refreshOnFailure"/> is for index-addressed edits (remove / re-tier a loot
        /// spot): a rejected index means the panel's view of the list is stale.
        /// </summary>
        // System.Func spelled out: Vintagestory.API.Common has a Func<> of its own, and both
        // namespaces are in scope here.
        private void PlaceEdit(IServerPlayer player, long placeId, System.Func<EventPlace, PlaceEditResult> edit,
            Action<EventPlace> afterEdit = null, bool refreshOnFailure = false)
        {
            if (!RequireAdmin(player)) return;
            if (!TryResolvePlace(player, placeId, out var place)) return;

            if (!ApplyPlaceEdit(player, place, edit(place)) && !refreshOnFailure) return;

            afterEdit?.Invoke(place);
            SendPlaceListTo(player);
        }

        /// <summary>Bounds-checks a spawn index against the place's list.</summary>
        private bool TryResolveSpawnIndex(IServerPlayer player, EventPlace place, int index)
        {
            var spawns = place.spawnPositions;
            if (spawns != null && index >= 0 && index < spawns.Count) return true;
            ReplyFail(player, Lang.Get("canevents:spawn-invalid-index"));
            return false;
        }

        /// <summary>The two corners of a bounds message, as the place's SetBounds wants them (it
        /// sorts them itself, so the order they arrive in doesn't matter).</summary>
        private static (Vintagestory.API.MathTools.BlockPos a, Vintagestory.API.MathTools.BlockPos b)
            BoundsCorners(int x1, int y1, int z1, int x2, int y2, int z2)
            => (new Vintagestory.API.MathTools.BlockPos(x1, y1, z1, 0),
                new Vintagestory.API.MathTools.BlockPos(x2, y2, z2, 0));

        /// <summary>Pushes the current place list.</summary>
        private void SendPlaceListTo(IServerPlayer player) => OnPlaceListRequest(player, null);

        private void OnPlaceListRequest(IServerPlayer player, RequestPlaceListMsg msg)
        {
            if (!RequireAdmin(player)) return;

            var placeManager = eventManager.eventPlacesManager;
            var entries = new List<PlaceListEntryMsg>();

            foreach (var place in placeManager.EventPlaces)
            {
                var spawns = place.spawnPositions;
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
                    BehaviorSummary = PlaceSummaryFormatter.Summarize(place)
                });
            }

            // Paired with the client-side receive log: disagreeing counts mean loss on the wire,
            // matching ones mean the panel isn't showing what it has.
            sapi.World.Logger.VerboseDebug(string.Format(
                "[canevents] PlaceList->{0}: sending {1} places, {2} with bounds: {3}",
                player.PlayerName, entries.Count, entries.Count(e => e.HasBounds),
                string.Join(", ", entries.Where(e => e.HasBounds)
                    .Select(e => e.Name + "#" + e.Id + "(" + e.BoundsMinX + "," + e.BoundsMinY + "," + e.BoundsMinZ + ")"))));

            serverChannel.SendPacket(new PlaceListMsg { Places = entries.ToArray() }, player);
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
                Name = msg.Name ?? "Unnamed",
                placeTypes = types.ToArray()
            };

            if (msg.UseBounds)
            {
                var (cornerA, cornerB) = BoundsCorners(msg.BoundsMinX, msg.BoundsMinY, msg.BoundsMinZ,
                                                       msg.BoundsMaxX, msg.BoundsMaxY, msg.BoundsMaxZ);
                place.SetBounds(cornerA, cornerB);

                bool isParkour = types.Contains(PlaceTags.Parkour);
                if (msg.NoAutoSpawns)
                {
                    // Caller places them itself.
                }
                else if (isParkour)
                {
                    // Parkour bounds usually span a tall course, so the top corners make poor spawns;
                    // the creator is typically standing at the intended start line.
                    place.spawnPositions.Add(player.Entity.Pos.AsBlockPos);
                }
                else
                {
                    // Spleef-style: two opposite edges just above the platform.
                    place.GenerateEdgeSpawns();
                }
            }
            else if (!msg.NoAutoSpawns)
            {
                place.spawnPositions.Add(player.Entity.Pos.AsBlockPos);
            }

            if (placeManager.AddPlace(place))
            {
                // The New Arena wizard follows this id to track the place it just made.
                ReplyOk(player, Lang.Get("canevents:place-created-id", place.Name, place.Id), place.Id);
                SendPlaceListTo(player);
            }
            else
            {
                ReplyFail(player, Lang.Get("canevents:place-create-failed"));
            }
        }

        /// <summary>Puts the admin at the place they picked: first spawn point, else bounds centre,
        /// else the KOTH hill. A place with none of those has no location at all.</summary>
        private void OnTeleportToPlace(IServerPlayer player, TeleportToPlaceMsg msg)
        {
            if (!RequireAdmin(player)) return;
            if (!TryResolvePlace(player, msg.PlaceId, out var place)) return;

            double x, y, z;
            string what;

            if (place.spawnPositions != null && place.spawnPositions.Count > 0)
            {
                var sp = place.spawnPositions[0];
                x = sp.X + 0.5; y = sp.Y; z = sp.Z + 0.5;
                what = "spawn point 1";
            }
            else if (place.HasBounds && place.BoundsMin != null && place.BoundsMax != null)
            {
                x = (place.BoundsMin.X + place.BoundsMax.X) / 2.0 + 0.5;
                // Top of the box, so you land on the floor rather than inside it.
                y = place.BoundsMax.Y + 1;
                z = (place.BoundsMin.Z + place.BoundsMax.Z) / 2.0 + 0.5;
                what = "bounds centre";
            }
            else if (place.TryGetBehaviorData(BehaviorKeys.KingOfTheHill, out var koth)
                     && JsonPos.TryRead(koth, out var hill))
            {
                x = hill.X + 0.5;
                y = hill.Y + 1;
                z = hill.Z + 0.5;
                what = "the hill";
            }
            else
            {
                // ASCII only: this reply is rendered by the admin panel's ImGui font, which has no
                // glyph for an em dash (it comes out as "?").
                ReplyFail(player, string.Format("'{0}' has no spawn points, bounds or hill - nowhere to teleport to.", place.Name));
                return;
            }

            player.Entity.TeleportToDouble(x, y, z);
            ReplyOk(player, string.Format("Teleported to '{0}' ({1}).", place.Name, what), place.Id);
        }

        private void OnSetPlaceBounds(IServerPlayer player, SetPlaceBoundsMsg msg)
        {
            if (!RequireAdmin(player)) return;
            if (!TryResolvePlace(player, msg.PlaceId, out var place)) return;
            if (!RequireNotOccupied(player, place)) return;

            var placeManager = eventManager.eventPlacesManager;

            if (msg.Clear)
            {
                place.HasBounds = false;
                place.BoundsMin = null;
                place.BoundsMax = null;
                placeManager.SavePlace(place);
                ReplyOk(player, string.Format("Bounds cleared on '{0}'. /canep commands can no longer target it.", place.Name), place.Id);
                SendPlaceListTo(player);
                return;
            }

            var (cornerA, cornerB) = BoundsCorners(msg.BoundsMinX, msg.BoundsMinY, msg.BoundsMinZ,
                                                   msg.BoundsMaxX, msg.BoundsMaxY, msg.BoundsMaxZ);
            place.SetBounds(cornerA, cornerB);

            // Same two-opposite-edges layout OnCreatePlace uses for bounded places.
            if (msg.GenerateSpawns) place.GenerateEdgeSpawns();

            var bMin = place.BoundsMin;
            var bMax = place.BoundsMax;
            placeManager.SavePlace(place);
            ReplyOk(player, string.Format("Bounds set on '{0}' ({1}x{2}x{3}){4}.",
                place.Name,
                bMax.X - bMin.X + 1, bMax.Y - bMin.Y + 1, bMax.Z - bMin.Z + 1,
                msg.GenerateSpawns ? ", 2 spawn points generated" : ""), place.Id);
            SendPlaceListTo(player);
        }

        private void OnAddSpawnPoint(IServerPlayer player, AddSpawnPointMsg msg)
        {
            if (!RequireAdmin(player)) return;
            if (!TryResolvePlace(player, msg.PlaceId, out var place)) return;

            var spawnPos = player.Entity.Pos.AsBlockPos;
            place.spawnPositions.Add(spawnPos);
            eventManager.eventPlacesManager.SavePlace(place);
            ReplyOk(player, Lang.Get("canevents:spawn-added", place.Name, place.spawnPositions.Count)
                + OutsideBoundsNote(place, spawnPos), place.Id);
            SendPlaceListTo(player);
        }

        private void OnMoveSpawnPoint(IServerPlayer player, MoveSpawnPointMsg msg)
        {
            if (!RequireAdmin(player)) return;
            if (!TryResolvePlace(player, msg.PlaceId, out var place)) return;
            if (!RequireNotOccupied(player, place)) return;
            if (!TryResolveSpawnIndex(player, place, msg.Index)) return;

            // Overwrite in place so the index (and therefore the team it belongs to) holds.
            var pos = player.Entity.Pos.AsBlockPos;
            place.spawnPositions[msg.Index] = pos;
            eventManager.eventPlacesManager.SavePlace(place);

            ReplyOk(player, string.Format("Spawn {0} on '{1}' moved to ({2}, {3}, {4}).",
                    msg.Index + 1, place.Name, pos.X, pos.Y, pos.Z)
                    + OutsideBoundsNote(place, pos), place.Id);
            SendPlaceListTo(player);
        }

        /// <summary>Flags a spawn placed outside the place's footprint. A warning, not a rejection:
        /// bounds aren't enforced during a match and starting just off the edge can be intended.</summary>
        private static string OutsideBoundsNote(EventPlace place, Vintagestory.API.MathTools.BlockPos pos)
        {
            if (!place.HasBounds || place.BoundsMin == null || place.BoundsMax == null) return "";
            bool inside = pos.X >= place.BoundsMin.X && pos.X <= place.BoundsMax.X
                       && pos.Z >= place.BoundsMin.Z && pos.Z <= place.BoundsMax.Z;
            return inside ? "" : " WARNING: that position is outside the place's bounds.";
        }

        private void OnRemoveSpawnPoint(IServerPlayer player, RemoveSpawnPointMsg msg)
        {
            if (!RequireAdmin(player)) return;
            if (!TryResolvePlace(player, msg.PlaceId, out var place)) return;
            if (!RequireNotOccupied(player, place)) return;
            if (!TryResolveSpawnIndex(player, place, msg.SpawnIndex)) return;

            var spawns = place.spawnPositions;
            spawns.RemoveAt(msg.SpawnIndex);
            eventManager.eventPlacesManager.SavePlace(place);
            ReplyOk(player, Lang.Get("canevents:spawn-removed", msg.SpawnIndex, spawns.Count));
            SendPlaceListTo(player);
        }

        private void OnDeletePlace(IServerPlayer player, DeletePlaceMsg msg)
        {
            if (!RequireAdmin(player)) return;

            // Refuse to delete a place hosting a live match: RunningEvent holds a reference, and
            // end-of-match teardown would then operate on a place no longer in the manager.
            if (TryResolvePlace(player, msg.PlaceId, out var existing) && !RequireNotOccupied(player, existing))
                return;

            if (eventManager.eventPlacesManager.DeletePlace(msg.PlaceId))
            {
                ReplyOk(player, Lang.Get("canevents:place-deleted-id", msg.PlaceId));
                SendPlaceListTo(player);
            }
            else
            {
                ReplyFail(player, Lang.Get("canevents:place-not-found-id", msg.PlaceId));
            }
        }

        private void OnTogglePlace(IServerPlayer player, TogglePlaceMsg msg)
        {
            if (!RequireAdmin(player)) return;
            if (!TryResolvePlace(player, msg.PlaceId, out var place)) return;

            place.Enabled = !place.Enabled;
            eventManager.eventPlacesManager.SavePlace(place);
            ReplyOk(player, Lang.Get(place.Enabled ? "canevents:place-toggled-enabled" : "canevents:place-toggled-disabled", place.Name));
            SendPlaceListTo(player);
        }
    }
}
