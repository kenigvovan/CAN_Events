using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers.events;
using canevents.src.EventPlacesManagers.Places;

namespace canevents.src.EventManagers
{
    // Place/event matching for the admin panel, commands and tournaments.
    public partial class CANEventManager
    {
        // allowedIds = tournament whitelist.
        public AvailabilityReport AvailabilityOf(CANEvent ev, ICollection<long> allowedIds = null)
            => PlaceAvailability.Survey(eventPlacesManager.EventPlaces, ev?.EventTypes, allowedIds);

        // Sorted by code (file load order isn't stable). No tag match -> all events of the mode,
        // so an untagged place can still be set up.
        public List<CANEvent> EventsForPlace<TBehavior>(EventPlace place) where TBehavior : class
        {
            var events = Events
                .Where(e => e.GetBehavior<TBehavior>() != null)
                .OrderBy(e => e.Code, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (place?.placeTypes == null) return events;

            var matching = events
                .Where(e => e.EventTypes != null && e.EventTypes.Count > 0 && place.HasAllTags(e.EventTypes))
                .ToList();
            return matching.Count > 0 ? matching : events;
        }

        // preferredCode wins even outside the place's tags. Ignores availability (places mid-setup rarely qualify).
        public CANEvent ResolveEventForPlace<TBehavior>(EventPlace place, string preferredCode = null)
            where TBehavior : class
        {
            var candidates = EventsForPlace<TBehavior>(place);
            if (!string.IsNullOrEmpty(preferredCode))
            {
                var picked = candidates.FirstOrDefault(
                    e => e.Code.Equals(preferredCode, StringComparison.OrdinalIgnoreCase));
                if (picked == null)
                {
                    var byCode = FindEventByCode(preferredCode);
                    if (byCode?.GetBehavior<TBehavior>() != null) picked = byCode;
                }
                if (picked != null) return picked;
            }
            return candidates.FirstOrDefault();
        }
    }
}
