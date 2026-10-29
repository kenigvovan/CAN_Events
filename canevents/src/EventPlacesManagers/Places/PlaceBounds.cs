using Vintagestory.API.MathTools;

namespace canevents.src.EventPlacesManagers.Places
{
    // Y is ignored: bounds are a floor-level slab (parkour goes up, spectators hover, items fly).
    public static class PlaceBounds
    {
        public static bool Has(EventPlace place)
            => place != null && place.HasBounds && place.BoundsMin != null && place.BoundsMax != null;

        // Raw form for the client, which only has a DTO.
        public static bool Contains(int minX, int maxX, int minZ, int maxZ, int x, int z)
            => x >= minX && x <= maxX && z >= minZ && z <= maxZ;

        // Arenas in different dimensions can share X/Z.
        public static bool Contains(int minX, int maxX, int minZ, int maxZ, int boundsDim,
            int x, int z, int dim)
            => dim == boundsDim && Contains(minX, maxX, minZ, maxZ, x, z);

        // False when the place has no bounds.
        public static bool Contains(EventPlace place, BlockPos pos)
            => Has(place) && pos != null
               && Contains(place.BoundsMin.X, place.BoundsMax.X, place.BoundsMin.Z, place.BoundsMax.Z,
                           place.BoundsMin.dimension, pos.X, pos.Z, pos.dimension);

        // dim is required so no caller silently means dimension 0.
        public static bool Contains(EventPlace place, double x, double z, int dim)
            => Has(place)
               && Contains(place.BoundsMin.X, place.BoundsMax.X, place.BoundsMin.Z, place.BoundsMax.Z,
                           place.BoundsMin.dimension,
                           (int)System.Math.Floor(x), (int)System.Math.Floor(z), dim);

        // +0.5 because block coords address the corner, not the centre.
        public static bool TryCentre(EventPlace place, out double x, out double y, out double z)
        {
            x = y = z = 0;
            if (!Has(place)) return false;

            var min = place.BoundsMin;
            var max = place.BoundsMax;
            x = (min.X + max.X) / 2.0 + 0.5;
            y = (min.Y + max.Y) / 2.0 + 0.5;
            z = (min.Z + max.Z) / 2.0 + 0.5;
            return true;
        }

        // Full world height, since bounds are a slab.
        public static bool TryCuboid(EventPlace place, int margin, int worldHeight,
            out BlockPos from, out BlockPos to)
        {
            from = to = null;
            if (!Has(place)) return false;

            var min = place.BoundsMin;
            var max = place.BoundsMax;
            from = new BlockPos(min.X - margin, 0, min.Z - margin, min.dimension);
            to = new BlockPos(max.X + margin, worldHeight, max.Z + margin, min.dimension);
            return true;
        }
    }
}
