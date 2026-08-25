using Vintagestory.API.MathTools;

namespace canevents.src.EventPlacesManagers.Places
{
    /// <summary>
    /// The footprint of an event place, and the one place that says what "inside" means.
    ///
    /// **Y is deliberately ignored.** Admins mark bounds by standing on the corners, so what gets
    /// stored is a floor-level slab rather than a volume: a parkour course runs upward, a spectator
    /// hovers above the arena, and an item thrown in the air is still in the arena. Checking Y would
    /// turn all three into "outside". This was written out three times in three files before, each
    /// with its own copy of that reasoning - now it lives here.
    /// </summary>
    public static class PlaceBounds
    {
        /// <summary>Does this place have a usable footprint at all?</summary>
        public static bool Has(EventPlace place)
            => place != null && place.HasBounds && place.BoundsMin != null && place.BoundsMax != null;

        /// <summary>
        /// The raw X/Z test, for callers holding plain numbers - the client works off a DTO rather
        /// than an EventPlace, and it has to answer exactly the same way the server does.
        /// </summary>
        public static bool Contains(int minX, int maxX, int minZ, int maxZ, int x, int z)
            => x >= minX && x <= maxX && z >= minZ && z <= maxZ;

        /// <summary>
        /// Is this position inside the footprint? False when the place has no bounds: "nowhere" does
        /// not contain anything. Callers that treat a place without bounds as unrestricted check
        /// <see cref="Has"/> first and skip the question.
        /// </summary>
        public static bool Contains(EventPlace place, BlockPos pos)
            => Has(place) && pos != null
               && Contains(place.BoundsMin.X, place.BoundsMax.X,
                           place.BoundsMin.Z, place.BoundsMax.Z, pos.X, pos.Z);

        /// <summary>Same test from world coordinates (an entity position, floored).</summary>
        public static bool Contains(EventPlace place, double x, double z)
            => Has(place)
               && Contains(place.BoundsMin.X, place.BoundsMax.X, place.BoundsMin.Z, place.BoundsMax.Z,
                           (int)System.Math.Floor(x), (int)System.Math.Floor(z));

        /// <summary>
        /// The middle of the footprint in world coordinates. The +0.5 is not decoration: block
        /// coordinates address a corner, so a radius measured from the raw value is off by half a
        /// block in every direction.
        /// </summary>
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

        /// <summary>
        /// The footprint as a cuboid for world queries: horizontally the bounds plus
        /// <paramref name="margin"/>, vertically the whole column, since the stored bounds are a
        /// slab and anything above the floor is still in the arena.
        /// </summary>
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
