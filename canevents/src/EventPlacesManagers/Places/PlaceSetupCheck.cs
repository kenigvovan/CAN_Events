using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Config;

namespace canevents.src.EventPlacesManagers.Places
{
    // Mode data a match can't run without. Optional extras (MM pickups, custom spleef platform) don't count.
    public static class PlaceSetupCheck
    {
        // Null tags means the place's own tags.
        public static List<string> Gaps(EventPlace place, IEnumerable<string> tags = null)
        {
            var gaps = new List<string>();
            if (place == null) return gaps;
            var set = new HashSet<string>(tags ?? place.placeTypes ?? new string[0]);

            if (set.Contains(PlaceTags.Parkour) && !HasParkourFinish(place))
                gaps.Add(Lang.Get("canevents:setup-gap-parkour-finish"));

            if (set.Contains(PlaceTags.Koth) && !HasCentreOrBounds(place, BehaviorKeys.KingOfTheHill))
                gaps.Add(Lang.Get("canevents:setup-gap-koth-hill"));

            if (set.Contains(PlaceTags.HungerGames) && !HasCentreOrBounds(place, BehaviorKeys.HungerGames))
                gaps.Add(Lang.Get("canevents:setup-gap-hg-zone"));

            if (set.Contains(PlaceTags.Ctf))
            {
                int posts = CtfPostCount(place);
                int needed = RequiredCtfPosts(place);
                if (posts < needed) gaps.Add(Lang.Get("canevents:setup-gap-ctf-posts", posts, needed));
            }

            // Team modes with a spawn per team; fewer spawns than teams puts two teams on one spot.
            if ((set.Contains(PlaceTags.Battle) || set.Contains(PlaceTags.OpenDeathmatch))
                && (place.spawnPositions?.Count ?? 0) < 2)
                gaps.Add(Lang.Get("canevents:setup-gap-team-spawns", place.spawnPositions?.Count ?? 0, 2));

            if (set.Contains(PlaceTags.Battle) && !PlaceConfigService.TryGetWaitingRoom(place, out _))
                gaps.Add(Lang.Get("canevents:setup-gap-waitroom"));
            return gaps;
        }

        public static string FirstGap(EventPlace place, IEnumerable<string> tags)
            => Gaps(place, tags).FirstOrDefault();

        private static bool HasParkourFinish(EventPlace place)
        {
            if (!place.TryGetBehaviorData(BehaviorKeys.Parkour, out var data)
                || !(data["Checkpoints"] is JArray arr)) return false;
            return arr.Any(t => t is JObject e && e.Value<bool>("IsFinish") && JsonPos.TryRead(e, out _));
        }

        // Same fallback as PlaceZone.TryResolveCentre.
        private static bool HasCentreOrBounds(EventPlace place, string key)
        {
            if (place.TryGetBehaviorData(key, out var data)
                && data["X"] != null && data["Y"] != null && data["Z"] != null) return true;
            return PlaceBounds.Has(place);
        }

        // Skipped team slots are empty objects and don't count.
        public static int CtfPostCount(EventPlace place)
        {
            if (!place.TryGetBehaviorData(BehaviorKeys.CaptureTheFlag, out var data)
                || !(data["Posts"] is JArray arr)) return 0;
            return arr.Count(t => JsonPos.TryRead(t, out _));
        }

        // Spawns are per team; a one-spawn test arena needs only one post.
        private static int RequiredCtfPosts(EventPlace place)
        {
            int spawns = place.spawnPositions?.Count ?? 0;
            return spawns <= 1 ? 1 : 2;
        }
    }
}
