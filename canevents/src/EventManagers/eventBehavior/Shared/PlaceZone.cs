using canevents.src.EventPlacesManagers.Places;

namespace canevents.src.EventManagers.eventBehavior.Shared
{
    /// <summary>Where a mode's zone centre came from; the caller logs accordingly.</summary>
    public enum ZoneOrigin
    {
        /// <summary>Read from the place's BehaviorData: what the admin configured.</summary>
        Configured,
        /// <summary>Fell back to the centre of the place's bounds.</summary>
        BoundsCentre,
        /// <summary>Neither available: the mode has no zone and must degrade gracefully.</summary>
        None
    }

    /// <summary>
    /// Resolves the centre point a zone-based mode plays around: the configured X/Y/Z from the
    /// place's BehaviorData, else the centre of its bounds, else nothing. Only the centre is
    /// shared; radius/height stay with the mode that understands them.
    /// </summary>
    public static class PlaceZone
    {
        public static ZoneOrigin TryResolveCentre(EventPlace place, string behaviorKey,
            out double x, out double y, out double z)
        {
            x = y = z = 0;
            if (place == null) return ZoneOrigin.None;

            // Block coordinates address a corner; +0.5 measures the radius from the block's middle.
            if (place.TryGetBehaviorData(behaviorKey, out var data)
                && data["X"] != null && data["Y"] != null && data["Z"] != null)
            {
                x = data.Value<double>("X") + 0.5;
                y = data.Value<double>("Y") + 0.5;
                z = data.Value<double>("Z") + 0.5;
                return ZoneOrigin.Configured;
            }

            // A misconfigured place still plays, instead of ending in a mute 0:0 draw.
            if (place.HasBounds && place.BoundsMin != null && place.BoundsMax != null)
            {
                x = (place.BoundsMin.X + place.BoundsMax.X) / 2.0 + 0.5;
                y = (place.BoundsMin.Y + place.BoundsMax.Y) / 2.0 + 0.5;
                z = (place.BoundsMin.Z + place.BoundsMax.Z) / 2.0 + 0.5;
                return ZoneOrigin.BoundsCentre;
            }

            return ZoneOrigin.None;
        }
    }
}
