using System;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace canevents.src.EventPlacesManagers.Places
{
    public partial class PlaceConfigService
    {
        // Spleef place edits; see PlaceConfigService.cs for the shared rules.

        // Spleef

        // centre is the admin's position: middle of the footprint, cage floor at its feet.
        // origin is the cage's min corner, for the caller's in-world preview.
        public PlaceEditResult SetSpleefCage(EventPlace place, BlockPos centre, int width, int depth, int height,
            string block, out BlockPos origin)
        {
            origin = null;
            if (!TryEdit(place, out var busy)) return busy;
            if (width < MinCageDim || depth < MinCageDim || height < MinCageDim
                || width > MaxCageDim || depth > MaxCageDim || height > MaxCageDim)
                return PlaceEditResult.Fail(Lang.Get("canevents:cage-dim-range", MinCageDim, MaxCageDim));

            origin = new BlockPos(centre.X - width / 2, centre.Y, centre.Z - depth / 2, centre.dimension);
            var data = place.GetOrCreateBehaviorData(BehaviorKeys.Spleef);
            data["CageOriginX"] = origin.X;
            data["CageOriginY"] = origin.Y;
            data["CageOriginZ"] = origin.Z;
            // Read back by SpleefBaseEB; absent in older places, which then mean dimension 0.
            data["CageOriginDim"] = origin.dimension;
            data["CageWidth"] = width;
            data["CageDepth"] = depth;
            data["CageHeight"] = height;
            data["CageBlock"] = string.IsNullOrEmpty(block) ? DefaultCageBlock : block;
            PersistIfSaved(place);

            return PlaceEditResult.Success(Lang.Get("canevents:cage-centered", centre, width, depth, height));
        }

        public PlaceEditResult ClearSpleefCage(EventPlace place)
        {
            if (!TryEdit(place, out var busy)) return busy;

            if (place.TryGetBehaviorData(BehaviorKeys.Spleef, out var data))
            {
                foreach (var key in new[] { "CageOriginX", "CageOriginY", "CageOriginZ", "CageOriginDim",
                                            "CageWidth", "CageDepth", "CageHeight", "CageBlock" })
                    data.Remove(key);
                if (data.Count == 0) place.BehaviorData.Remove(BehaviorKeys.Spleef);
            }
            PersistIfSaved(place);
            return PlaceEditResult.Success(Lang.Get("canevents:cage-cleared"));
        }

        // Replaces the whole platform (the platform editor's session).
        public PlaceEditResult SaveSpleefPlatform(EventPlace place, System.Collections.Generic.ICollection<BlockPos> positions)
        {
            if (!TryEdit(place, out var busy)) return busy;

            int n = positions?.Count ?? 0;
            var xs = new int[n];
            var ys = new int[n];
            var zs = new int[n];
            var ds = new int[n];
            int i = 0;
            if (positions != null)
                foreach (var p in positions) { xs[i] = p.X; ys[i] = p.Y; zs[i] = p.Z; ds[i] = p.dimension; i++; }

            var data = place.GetOrCreateBehaviorData(BehaviorKeys.Spleef);
            data["PlatformX"] = JToken.FromObject(xs);
            data["PlatformY"] = JToken.FromObject(ys);
            data["PlatformZ"] = JToken.FromObject(zs);
            data["PlatformDim"] = JToken.FromObject(ds);
            PersistIfSaved(place);

            return PlaceEditResult.Success(Lang.Get("canevents:platform-saved", n));
        }
    }
}
