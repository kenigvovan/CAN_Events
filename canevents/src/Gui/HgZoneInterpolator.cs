using System;
using canevents.src.Network;

namespace canevents.src.Gui
{
    /// <summary>
    /// Turns the server's occasional <see cref="HgZoneStateMsg"/> into a per-frame radius. Single
    /// source for the HUD and the dome renderer, which used to interpolate separately and disagree.
    /// </summary>
    public static class HgZoneInterpolator
    {
        /// <summary>
        /// Current radius: linear from RadiusNow to RadiusTarget over SecondsToTarget, clamped.
        /// <paramref name="sinceSeconds"/> is packet age by the client's own clock.
        /// </summary>
        public static float RadiusAt(HgZoneStateMsg zone, float sinceSeconds)
        {
            if (zone == null) return 0f;
            if (zone.SecondsToTarget <= 0f) return zone.RadiusNow;

            float t = Clamp01(sinceSeconds / zone.SecondsToTarget);
            return zone.RadiusNow + (zone.RadiusTarget - zone.RadiusNow) * t;
        }

        /// <summary>Seconds until the zone reaches its target size, never negative.</summary>
        public static float SecondsLeft(HgZoneStateMsg zone, float sinceSeconds)
            => zone == null ? 0f : Math.Max(0f, zone.SecondsToTarget - sinceSeconds);

        /// <summary>Zone is heading to a smaller size; the epsilon keeps a held size from
        /// reading as a shrink.</summary>
        public static bool IsShrinking(HgZoneStateMsg zone)
            => zone != null && zone.RadiusTarget < zone.RadiusNow - 0.01f;

        /// <summary>Distance from (x, z) to the storm wall: positive inside, negative outside.</summary>
        public static double DistanceToBorder(HgZoneStateMsg zone, float radius, double x, double z)
        {
            if (zone == null) return 0;
            double dx = x - zone.X, dz = z - zone.Z;
            return radius - Math.Sqrt(dx * dx + dz * dz);
        }

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
    }
}
