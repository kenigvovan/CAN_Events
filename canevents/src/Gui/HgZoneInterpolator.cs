using System;
using canevents.src.Network;

namespace canevents.src.Gui
{
    // Shared by the HUD and the dome so both show the same radius.
    public static class HgZoneInterpolator
    {
        // sinceSeconds is packet age by the client clock.
        public static float RadiusAt(HgZoneStateMsg zone, float sinceSeconds)
        {
            if (zone == null) return 0f;
            if (zone.SecondsToTarget <= 0f) return zone.RadiusNow;

            float t = Clamp01(sinceSeconds / zone.SecondsToTarget);
            return zone.RadiusNow + (zone.RadiusTarget - zone.RadiusNow) * t;
        }

        public static float SecondsLeft(HgZoneStateMsg zone, float sinceSeconds)
            => zone == null ? 0f : Math.Max(0f, zone.SecondsToTarget - sinceSeconds);

        public static bool IsShrinking(HgZoneStateMsg zone)
            => zone != null && zone.RadiusTarget < zone.RadiusNow - 0.01f;

        // Positive inside, negative outside.
        public static double DistanceToBorder(HgZoneStateMsg zone, float radius, double x, double z)
        {
            if (zone == null) return 0;
            double dx = x - zone.X, dz = z - zone.Z;
            return radius - Math.Sqrt(dx * dx + dz * dz);
        }

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
    }
}
