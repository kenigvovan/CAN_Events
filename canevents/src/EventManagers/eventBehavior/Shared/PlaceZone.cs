using canevents.src.EventPlacesManagers.Places;

namespace canevents.src.EventManagers.eventBehavior.Shared
{
    public enum ZoneOrigin
    {
        Configured,
        BoundsCentre,
        None
    }

    // Zone centre: BehaviorData X/Y/Z, else bounds centre.
    public static class PlaceZone
    {
        public static ZoneOrigin TryResolveCentre(EventPlace place, string behaviorKey,
            out double x, out double y, out double z)
        {
            x = y = z = 0;
            if (place == null) return ZoneOrigin.None;

            // +0.5: block centre.
            if (place.TryGetBehaviorData(behaviorKey, out var data)
                && data["X"] != null && data["Y"] != null && data["Z"] != null)
            {
                x = data.Value<double>("X") + 0.5;
                y = data.Value<double>("Y") + 0.5;
                z = data.Value<double>("Z") + 0.5;
                return ZoneOrigin.Configured;
            }

            if (PlaceBounds.TryCentre(place, out x, out y, out z))
                return ZoneOrigin.BoundsCentre;

            return ZoneOrigin.None;
        }
    }
}
