using System.Collections.Generic;
using System.Linq;

namespace canevents.src.EventPlacesManagers.Places
{
    public enum PlaceProblem
    {
        None,
        MissingTags,
        // Not in the tournament's arena whitelist.
        OutsideWhitelist,
        Disabled,
        NoSpawns,
        // Mode data missing, see PlaceSetupCheck.
        IncompleteSetup,
        Occupied,
        // ArenaBlockJournal still has blocks to put back.
        PendingRestore
    }

    // Shared by the admin panel, tournaments and the scheduler so they report the same thing.
    public sealed class AvailabilityReport
    {
        public string[] Tags = new string[0];
        public int Tagged;
        public int Free;
        public int Occupied;
        public int PendingRestore;
        public int OutsideWhitelist;
        public int Disabled;
        public int NoSpawns;
        public int IncompleteSetup;
        public EventPlace FirstFree;
        public EventPlace FirstWithoutSpawns;
        public EventPlace FirstIncomplete;
        public string FirstIncompleteGap;

        public bool AnyFree => Free > 0;

        // e.g. "3 matching arena(s), 2 in use, 1 disabled"
        public string Summary
        {
            get
            {
                if (Tagged == 0) return "no arena tagged " + TagText;

                var detail = new List<string>();
                if (Free > 0) detail.Add(Free + " free");
                if (Occupied > 0) detail.Add(Occupied + " in use");
                if (PendingRestore > 0) detail.Add(PendingRestore + " still being restored");
                if (OutsideWhitelist > 0) detail.Add(OutsideWhitelist + " outside this tournament's arena list");
                if (Disabled > 0) detail.Add(Disabled + " disabled");
                if (NoSpawns > 0) detail.Add(NoSpawns + " without spawn points");
                if (IncompleteSetup > 0)
                    detail.Add(IncompleteSetup + " not fully set up ('" + (FirstIncomplete?.Name ?? "?") + "': "
                        + FirstIncompleteGap + ")");
                return Tagged + " matching arena(s), " + (detail.Count == 0 ? "none free" : string.Join(", ", detail));
            }
        }

        public string TagText => string.Join("+", Tags);
    }

    public static class PlaceAvailability
    {
        // Checked in the order an admin would fix them, transient states last.
        public static PlaceProblem Diagnose(EventPlace place, IEnumerable<string> tags,
            ICollection<long> allowedIds = null)
        {
            if (place == null || !place.HasAllTags(tags)) return PlaceProblem.MissingTags;
            if (allowedIds != null && allowedIds.Count > 0 && !allowedIds.Contains(place.Id))
                return PlaceProblem.OutsideWhitelist;
            if (!place.Enabled) return PlaceProblem.Disabled;
            if (place.spawnPositions == null || place.spawnPositions.Count == 0) return PlaceProblem.NoSpawns;
            if (PlaceSetupCheck.FirstGap(place, tags) != null) return PlaceProblem.IncompleteSetup;
            if (place.Occupied) return PlaceProblem.Occupied;
            if (EventManagers.eventBehavior.Shared.ArenaBlockJournal.IsPendingRestore(place.Id))
                return PlaceProblem.PendingRestore;
            return PlaceProblem.None;
        }

        public static AvailabilityReport Survey(IEnumerable<EventPlace> places, IEnumerable<string> tags,
            ICollection<long> allowedIds = null)
        {
            var tagList = tags?.ToArray() ?? new string[0];
            var report = new AvailabilityReport { Tags = tagList };
            if (places == null) return report;

            foreach (var p in places)
            {
                var problem = Diagnose(p, tagList, allowedIds);
                if (problem == PlaceProblem.MissingTags) continue;
                report.Tagged++;
                switch (problem)
                {
                    case PlaceProblem.None:
                        report.Free++;
                        report.FirstFree ??= p;
                        break;
                    case PlaceProblem.Occupied: report.Occupied++; break;
                    case PlaceProblem.PendingRestore: report.PendingRestore++; break;
                    case PlaceProblem.OutsideWhitelist: report.OutsideWhitelist++; break;
                    case PlaceProblem.Disabled: report.Disabled++; break;
                    case PlaceProblem.NoSpawns:
                        report.NoSpawns++;
                        report.FirstWithoutSpawns ??= p;
                        break;
                    case PlaceProblem.IncompleteSetup:
                        report.IncompleteSetup++;
                        if (report.FirstIncomplete == null)
                        {
                            report.FirstIncomplete = p;
                            report.FirstIncompleteGap = PlaceSetupCheck.FirstGap(p, tagList);
                        }
                        break;
                }
            }
            return report;
        }
    }
}
