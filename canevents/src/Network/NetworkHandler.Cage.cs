using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventPlacesManagers.Places;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace canevents.src.Network
{
    // Server-side Spleef cage editor: place a glass-shell preview around the player, persist its
    // dimensions into the Spleef BehaviorData, and roll the preview blocks back on clear/match start.
    public partial class ServerNetworkHandler
    {
        // Tracks blocks replaced by cage previews so they can be restored. Key = placeId.
        private readonly Dictionary<long, List<(Vintagestory.API.MathTools.BlockPos pos, int blockId)>> _cagePreview
            = new Dictionary<long, List<(Vintagestory.API.MathTools.BlockPos, int)>>();

        private void OnSetCagePos(IServerPlayer player, SetCagePosMsg msg)
        {
            if (!RequireAdmin(player)) return;
            if (!TryResolvePlace(player, msg.PlaceId, out var place)) return;

            if (msg.Width < MinCageDim || msg.Depth < MinCageDim || msg.Height < MinCageDim
                || msg.Width > MaxCageDim || msg.Depth > MaxCageDim || msg.Height > MaxCageDim)
            {
                ReplyFail(player, Lang.Get("canevents:cage-dim-range", MinCageDim, MaxCageDim));
                return;
            }

            RestoreCagePreview(msg.PlaceId);

            // Centre the cage on the player, who stands in the middle of the footprint.
            var playerPos = player.Entity.Pos.AsBlockPos;
            var origin = new Vintagestory.API.MathTools.BlockPos(
                playerPos.X - msg.Width / 2,
                playerPos.Y,
                playerPos.Z - msg.Depth / 2,
                playerPos.dimension);
            var data = GetOrCreateSpleefData(place);
            data["CageOriginX"] = origin.X;
            data["CageOriginY"] = origin.Y;
            data["CageOriginZ"] = origin.Z;
            data["CageWidth"]   = msg.Width;
            data["CageDepth"]   = msg.Depth;
            data["CageHeight"]  = msg.Height;
            data["CageBlock"]   = msg.Block ?? "game:glass-plain";
            eventManager.eventPlacesManager.SavePlace(place);

            BuildCagePreview(msg.PlaceId, origin, msg.Width, msg.Depth, msg.Height, msg.Block ?? "game:glass-plain");

            ReplyOk(player, Lang.Get("canevents:cage-centered", playerPos, msg.Width, msg.Depth, msg.Height));
        }

        private void OnClearCagePos(IServerPlayer player, ClearCagePosMsg msg)
        {
            if (!RequireAdmin(player)) return;
            if (!TryResolvePlace(player, msg.PlaceId, out var place)) return;

            RestoreCagePreview(msg.PlaceId);
            if (place.TryGetBehaviorData(BehaviorKeys.Spleef, out var spleefData))
            {
                spleefData.Remove("CageOriginX"); spleefData.Remove("CageOriginY"); spleefData.Remove("CageOriginZ");
                spleefData.Remove("CageWidth"); spleefData.Remove("CageDepth"); spleefData.Remove("CageHeight");
                spleefData.Remove("CageBlock");
                if (spleefData.Count == 0) place.BehaviorData.Remove(BehaviorKeys.Spleef);
            }
            eventManager.eventPlacesManager.SavePlace(place);

            ReplyOk(player, Lang.Get("canevents:cage-cleared"));
        }

        private void BuildCagePreview(long placeId, Vintagestory.API.MathTools.BlockPos origin, int w, int d, int h, string blockCode)
        {
            var blockAccessor = sapi.World.BlockAccessor;
            var block = sapi.World.GetBlock(new Vintagestory.API.Common.AssetLocation(blockCode));
            if (block == null || block.BlockId == 0)
            {
                sapi.World.Logger.Error("[Spleef] Cage preview block '{0}' not found.", blockCode);
                return;
            }

            var saved = new List<(Vintagestory.API.MathTools.BlockPos, int)>();
            // Registered early so a crash mid-build still leaves the next RestoreCagePreview
            // able to roll back what was already placed.
            _cagePreview[placeId] = saved;

            try
            {
                for (int x = 0; x < w; x++)
                for (int y = 0; y < h; y++)
                for (int z = 0; z < d; z++)
                {
                    bool isShell = x == 0 || x == w - 1 || y == 0 || y == h - 1 || z == 0 || z == d - 1;
                    var pos = new Vintagestory.API.MathTools.BlockPos(origin.X + x, origin.Y + y, origin.Z + z, origin.dimension);
                    saved.Add((pos.Copy(), blockAccessor.GetBlock(pos).BlockId));
                    blockAccessor.SetBlock(isShell ? block.BlockId : 0, pos);
                }
                blockAccessor.Commit();
            }
            catch (Exception ex)
            {
                sapi.World.Logger.Error("[Spleef] BuildCagePreview failed, rolling back: {0}", ex);
                RestoreCagePreview(placeId);
                throw;
            }
        }

        /// <summary>Rolls back every active cage preview. Called from Dispose: nothing after a
        /// reload remembers the replaced blocks, so the glass shell would stay forever.</summary>
        public void RestoreAllCagePreviews()
        {
            foreach (var placeId in _cagePreview.Keys.ToList())
            {
                try { RestoreCagePreview(placeId); }
                catch (Exception ex)
                {
                    sapi.World.Logger.Error("RestoreAllCagePreviews for place {0}: {1}", placeId, ex);
                }
            }
        }

        public void RestoreCagePreview(long placeId)
        {
            if (!_cagePreview.TryGetValue(placeId, out var saved)) return;

            var blockAccessor = sapi.World.BlockAccessor;
            foreach (var (pos, blockId) in saved)
                blockAccessor.SetBlock(blockId, pos);

            blockAccessor.Commit();
            _cagePreview.Remove(placeId);
        }
    }
}
