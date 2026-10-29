using Vintagestory.API.Common.Entities;

namespace canevents.src.EventManagers.eventBehavior.Shared
{
    // Cylinder, not sphere, so a bridge above the zone doesn't count.
    public static class CylinderZone
    {
        // Ignores height.
        public static bool WithinRadius(EntityPos pos, double centreX, double centreZ, double radius)
        {
            double dx = pos.X - centreX;
            double dz = pos.Z - centreZ;
            return dx * dx + dz * dz <= radius * radius;
        }

        public static bool Contains(EntityPos pos, double centreX, double centreY, double centreZ,
            double radius, double below, double above)
        {
            double dy = pos.Y - centreY;
            return dy >= -below && dy <= above && WithinRadius(pos, centreX, centreZ, radius);
        }
    }
}
