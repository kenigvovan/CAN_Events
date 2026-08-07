using System.Collections.Generic;
using System.Linq;
using canevents.src.EventPlacesManagers.Places;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace canevents.src.Network
{
    // Server-side Spleef platform editor: live block-place/break tracking, shape fill / flood-fill
    // helpers, and persistence of platform positions into the place's Spleef BehaviorData.
    public partial class ServerNetworkHandler
    {
        // Active platform edit sessions. Key = playerUid.
        private readonly Dictionary<string, PlatformEditSession> _platformSessions
            = new Dictionary<string, PlatformEditSession>();

        private class PlatformEditSession
        {
            public long PlaceId;
            public HashSet<Vintagestory.API.MathTools.BlockPos> Positions = new HashSet<Vintagestory.API.MathTools.BlockPos>();
        }

        /// <summary>Drops the world-event hooks and any open edit sessions. Called from Dispose:
        /// the hooks are lazy, so a reload during editing would leave a dead handler behind.</summary>
        private void UnhookPlatformEditing()
        {
            if (_blockEventsHooked)
            {
                sapi.Event.DidPlaceBlock -= OnPlayerPlacedBlock;
                sapi.Event.DidBreakBlock -= OnPlayerBrokeBlock;
                _blockEventsHooked = false;
            }
            _platformSessions.Clear();
        }

        private void OnPlatformEditStart(IServerPlayer player, PlatformEditStartMsg msg)
        {
            if (!RequireAdmin(player)) return;
            if (!TryResolvePlace(player, msg.PlaceId, out var place)) return;

            var session = new PlatformEditSession { PlaceId = msg.PlaceId };

            if (place.TryGetBehaviorData(BehaviorKeys.Spleef, out var data) && data["PlatformX"] != null)
            {
                var xs = data["PlatformX"].ToObject<int[]>();
                var ys = data["PlatformY"].ToObject<int[]>();
                var zs = data["PlatformZ"].ToObject<int[]>();
                var ds = data["PlatformDim"]?.ToObject<int[]>();
                if (xs != null)
                    for (int i = 0; i < xs.Length; i++)
                        session.Positions.Add(new Vintagestory.API.MathTools.BlockPos(xs[i], ys[i], zs[i], ds != null ? ds[i] : 0));
            }

            bool wasActive = _platformSessions.ContainsKey(player.PlayerUID);
            _platformSessions[player.PlayerUID] = session;

            if (!_blockEventsHooked)
            {
                sapi.Event.DidPlaceBlock  += OnPlayerPlacedBlock;
                sapi.Event.DidBreakBlock  += OnPlayerBrokeBlock;
                _blockEventsHooked = true;
            }

            SendPlatformState(player, msg.PlaceId, true, session.Positions.Count);
            var startMsg = wasActive
                ? Lang.Get("canevents:platform-edit-restarted")
                : Lang.Get("canevents:platform-edit-started");
            ReplyOk(player, startMsg);
        }

        private void OnPlatformEditStop(IServerPlayer player, PlatformEditStopMsg msg)
        {
            // Gated like every other mutating platform handler, for consistency.
            if (!RequireAdmin(player)) return;
            if (!_platformSessions.TryGetValue(player.PlayerUID, out var session)) return;
            _platformSessions.Remove(player.PlayerUID);

            if (_platformSessions.Count == 0 && _blockEventsHooked)
            {
                sapi.Event.DidPlaceBlock -= OnPlayerPlacedBlock;
                sapi.Event.DidBreakBlock -= OnPlayerBrokeBlock;
                _blockEventsHooked = false;
            }

            if (msg.Save)
            {
                var place = eventManager.eventPlacesManager.FindById(session.PlaceId);
                if (place != null)
                {
                    SavePlatformPositions(place, session.Positions);
                    ReplyOk(player, Lang.Get("canevents:platform-saved", session.Positions.Count));
                }
            }
            else
            {
                ReplyOk(player, Lang.Get("canevents:platform-edit-cancelled"));
            }

            SendPlatformState(player, session.PlaceId, false, 0);
        }

        private void OnPlatformFillShape(IServerPlayer player, PlatformFillShapeMsg msg)
        {
            if (!RequireAdmin(player)) return;
            if (!_platformSessions.TryGetValue(player.PlayerUID, out var session)) return;

            var block = sapi.World.GetBlock(new Vintagestory.API.Common.AssetLocation(msg.BlockCode ?? "game:stickslayer-wooden-ew-free"));
            if (block == null) return;

            var origin = player.Entity.Pos.AsBlockPos;
            int y = origin.Y;
            var blockAccessor = sapi.World.BlockAccessor;
            // Cap the radius like flood-fill does: unbounded, this touches (2r+1)^2 blocks
            // synchronously on the main thread and freezes the server.
            var fillCfg = sapi.ModLoader.GetModSystem<canevents>().config;
            int r = System.Math.Min(System.Math.Max(1, msg.Radius),
                System.Math.Max(1, fillCfg.maxPlatformFloodFillRadius));

            int added = 0, skippedNonAir = 0, duplicates = 0;
            for (int x = -r; x <= r; x++)
            for (int z = -r; z <= r; z++)
            {
                if (msg.Circle && x * x + z * z > r * r) continue;

                var pos = new Vintagestory.API.MathTools.BlockPos(origin.X + x, y, origin.Z + z, origin.dimension);
                if (blockAccessor.GetBlock(pos).BlockId != 0) { skippedNonAir++; continue; }

                blockAccessor.SetBlock(block.BlockId, pos);
                bool wasNew = session.Positions.Add(pos.Copy());
                if (wasNew) added++; else duplicates++;
            }

            blockAccessor.Commit();
            sapi.World.Logger.VerboseDebug(
                "[PlatformFill] player={0} origin=({1},{2},{3}) shape={4} r={5} block={6} added={7} skippedNonAir={8} dupes={9} totalNow={10}",
                player.PlayerName, origin.X, y, origin.Z, msg.Circle ? "circle" : "square", r, msg.BlockCode,
                added, skippedNonAir, duplicates, session.Positions.Count);
            SendPlatformState(player, session.PlaceId, true, session.Positions.Count);
        }

        private void OnPlatformFloodFill(IServerPlayer player, PlatformFloodFillMsg msg)
        {
            if (!RequireAdmin(player)) return;
            if (!_platformSessions.TryGetValue(player.PlayerUID, out var session)) return;

            var block = sapi.World.GetBlock(new Vintagestory.API.Common.AssetLocation(msg.BlockCode ?? "game:stickslayer-wooden-ew-free"));
            if (block == null) return;

            var cfg = sapi.ModLoader.GetModSystem<canevents>().config;
            int hardRadiusCap = System.Math.Max(1, cfg.maxPlatformFloodFillRadius);
            int hardBlocksCap = System.Math.Max(1, cfg.maxPlatformFloodFillBlocks);

            var origin = player.Entity.Pos.AsBlockPos;
            int y = origin.Y;
            var blockAccessor = sapi.World.BlockAccessor;
            int requestedR = System.Math.Max(1, msg.MaxRadius);
            int maxR = System.Math.Min(requestedR, hardRadiusCap);
            int maxR2 = maxR * maxR;
            bool radiusClamped = requestedR > hardRadiusCap;

            // BFS on the 2D plane at y = origin.Y.
            var visited = new HashSet<long>();
            long Key(int x, int z) => ((long)(x + 30000000) << 30) | (uint)(z + 30000000);

            var queue = new Queue<(int x, int z)>();
            queue.Enqueue((origin.X, origin.Z));
            visited.Add(Key(origin.X, origin.Z));

            int added = 0, duplicates = 0, boundaryHits = 0, outOfRange = 0;
            bool blockCapReached = false;

            while (queue.Count > 0)
            {
                var (x, z) = queue.Dequeue();

                int dx = x - origin.X;
                int dz = z - origin.Z;
                if (dx * dx + dz * dz > maxR2) { outOfRange++; continue; }

                var pos = new Vintagestory.API.MathTools.BlockPos(x, y, z, origin.dimension);
                int existing = blockAccessor.GetBlock(pos).BlockId;
                if (existing != 0) { boundaryHits++; continue; }

                if (added >= hardBlocksCap) { blockCapReached = true; break; }

                blockAccessor.SetBlock(block.BlockId, pos);
                bool wasNew = session.Positions.Add(pos.Copy());
                if (wasNew) added++; else duplicates++;

                foreach (var (nx, nz) in new[] { (x + 1, z), (x - 1, z), (x, z + 1), (x, z - 1) })
                {
                    var k = Key(nx, nz);
                    if (visited.Contains(k)) continue;
                    visited.Add(k);
                    queue.Enqueue((nx, nz));
                }
            }

            blockAccessor.Commit();
            sapi.World.Logger.VerboseDebug(
                "[PlatformFloodFill] player={0} origin=({1},{2},{3}) maxR={4} block={5} added={6} dupes={7} boundaryHits={8} outOfRange={9} totalNow={10} capHit={11} radiusClamped={12}",
                player.PlayerName, origin.X, y, origin.Z, maxR, msg.BlockCode,
                added, duplicates, boundaryHits, outOfRange, session.Positions.Count, blockCapReached, radiusClamped);

            if (radiusClamped || blockCapReached)
            {
                var msgParts = new List<string>();
                if (radiusClamped) msgParts.Add(Lang.Get("canevents:platform-floodfill-radius-clamped", hardRadiusCap));
                if (blockCapReached) msgParts.Add(Lang.Get("canevents:platform-floodfill-cap-hit", hardBlocksCap));
                ReplyOk(player, Lang.Get("canevents:platform-floodfill-result", added, string.Join(", ", msgParts)));
            }
            SendPlatformState(player, session.PlaceId, true, session.Positions.Count);
        }

        private bool _blockEventsHooked = false;

        private void OnPlayerDisconnect(IServerPlayer player)
        {
            if (!_platformSessions.Remove(player.PlayerUID)) return;
            if (_platformSessions.Count == 0 && _blockEventsHooked)
            {
                sapi.Event.DidPlaceBlock -= OnPlayerPlacedBlock;
                sapi.Event.DidBreakBlock -= OnPlayerBrokeBlock;
                _blockEventsHooked = false;
            }
        }

        private void OnPlayerPlacedBlock(IServerPlayer player, int oldBlockId, Vintagestory.API.Common.BlockSelection sel, Vintagestory.API.Common.ItemStack stack)
        {
            if (!_platformSessions.TryGetValue(player.PlayerUID, out var session)) return;
            session.Positions.Add(sel.Position.Copy());
            SendPlatformState(player, session.PlaceId, true, session.Positions.Count);
        }

        private void OnPlayerBrokeBlock(IServerPlayer player, int oldBlockId, Vintagestory.API.Common.BlockSelection sel)
        {
            if (!_platformSessions.TryGetValue(player.PlayerUID, out var session)) return;
            session.Positions.RemoveWhere(p => p.Equals(sel.Position));
            SendPlatformState(player, session.PlaceId, true, session.Positions.Count);
        }

        private void SendPlatformState(IServerPlayer player, long placeId, bool active, int count)
        {
            serverChannel.SendPacket(new PlatformEditStateMsg { PlaceId = placeId, Active = active, Count = count }, player);
        }

        private static Newtonsoft.Json.Linq.JObject GetOrCreateSpleefData(EventPlacesManagers.Places.EventPlace place)
            => place.GetOrCreateBehaviorData(BehaviorKeys.Spleef);

        private void SavePlatformPositions(EventPlacesManagers.Places.EventPlace place, HashSet<Vintagestory.API.MathTools.BlockPos> positions)
        {
            var data = GetOrCreateSpleefData(place);
            var xs = new int[positions.Count];
            var ys = new int[positions.Count];
            var zs = new int[positions.Count];
            var ds = new int[positions.Count];
            int i = 0;
            foreach (var p in positions) { xs[i] = p.X; ys[i] = p.Y; zs[i] = p.Z; ds[i] = p.dimension; i++; }

            data["PlatformX"] = Newtonsoft.Json.Linq.JToken.FromObject(xs);
            data["PlatformY"] = Newtonsoft.Json.Linq.JToken.FromObject(ys);
            data["PlatformZ"] = Newtonsoft.Json.Linq.JToken.FromObject(zs);
            data["PlatformDim"] = Newtonsoft.Json.Linq.JToken.FromObject(ds);

            var yGroups = new Dictionary<int, int>();
            foreach (var p in positions)
            {
                if (!yGroups.ContainsKey(p.Y)) yGroups[p.Y] = 0;
                yGroups[p.Y]++;
            }
            var summary = string.Join(", ", yGroups.OrderBy(kv => kv.Key).Select(kv => "Y=" + kv.Key + ":" + kv.Value));
            sapi.World.Logger.VerboseDebug("[PlatformSave] place='{0}' id={1} total={2} byLevel: {3}",
                place.Name, place.Id, positions.Count, summary);

            eventManager.eventPlacesManager.SavePlace(place);
        }
    }
}
