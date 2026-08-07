using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace canevents.src.EventPlacesManagers.Places
{
    /// <summary>
    /// Turns a place's per-behavior data into short display lines for the admin panel - a readout,
    /// not a transfer format. Lives here, not in the network layer, because it encodes what each
    /// game mode's BehaviorData means; adding a mode should not touch a networking file.
    /// </summary>
    public static class PlaceSummaryFormatter
    {
        public static string[] Summarize(EventPlace place)
        {
            var lines = new List<string>();

            if (place.TryGetBehaviorData(BehaviorKeys.KingOfTheHill, out var koth)
                && JsonPos.TryRead(koth, out var hill))
            {
                lines.Add(string.Format(CultureInfo.InvariantCulture, "hill @ {0},{1},{2} r={3} h={4}",
                    hill.X, hill.Y, hill.Z,
                    koth["Radius"] != null ? koth.Value<float>("Radius") : 0f,
                    koth["Height"] != null ? koth.Value<float>("Height") : 0f));
            }

            if (place.TryGetBehaviorData(BehaviorKeys.Parkour, out var park))
            {
                int cps = (park["Checkpoints"] as JArray)?.Count ?? 0;
                if (cps > 0) lines.Add("checkpoints: " + cps);
                if (park["Lives"] != null) lines.Add("lives: " + park.Value<int>("Lives"));
            }

            if (place.TryGetBehaviorData(BehaviorKeys.HungerGames, out var hg))
            {
                if (JsonPos.TryRead(hg, out var zone))
                {
                    lines.Add(string.Format(CultureInfo.InvariantCulture, "zone @ {0},{1},{2} r={3}",
                        zone.X, zone.Y, zone.Z,
                        hg["InitialRadius"] != null ? hg.Value<float>("InitialRadius") : 0f));
                }
                int loot = (hg["LootSpots"] as JArray)?.Count ?? 0;
                if (loot > 0) lines.Add("loot spots: " + loot);
            }

            if (place.TryGetBehaviorData(BehaviorKeys.CaptureTheFlag, out var ctf)
                && ctf["Posts"] is JArray postArr)
            {
                // One line per post with coordinates: the panel has no other view of this data.
                // Gaps are listed too - an empty slot is a team with no post, not a smaller match.
                for (int i = 0; i < postArr.Count; i++)
                {
                    lines.Add(JsonPos.TryRead(postArr[i], out var post)
                        ? string.Format("ctf post {0} @ {1},{2},{3}", i + 1, post.X, post.Y, post.Z)
                        : string.Format("ctf post {0}: NOT SET", i + 1));
                }
            }

            if (place.TryGetBehaviorData(BehaviorKeys.Spleef, out var spleef))
            {
                int blocks = (spleef["PlatformX"] as JArray)?.Count ?? 0;
                if (blocks > 0) lines.Add("platform: " + blocks + " blocks");
                if (spleef["CageOriginX"] != null)
                {
                    lines.Add(string.Format("cage: {0}x{1}x{2}",
                        spleef["CageWidth"] != null ? spleef.Value<int>("CageWidth") : 5,
                        spleef["CageDepth"] != null ? spleef.Value<int>("CageDepth") : 5,
                        spleef["CageHeight"] != null ? spleef.Value<int>("CageHeight") : 4));
                }
            }

            return lines.ToArray();
        }

        /// <summary>"x,y,z,r,h" for the admin panel's hill overlay, or null when no hill is set.</summary>
        public static string PackKothHill(EventPlace place)
        {
            if (!place.TryGetBehaviorData(BehaviorKeys.KingOfTheHill, out var data)) return null;
            if (!JsonPos.TryRead(data, out var hill)) return null;

            float radius = data["Radius"] != null ? data.Value<float>("Radius") : 0f;
            float height = data["Height"] != null ? data.Value<float>("Height") : 0f;
            return string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3},{4}",
                hill.X, hill.Y, hill.Z, radius, height);
        }
    }
}
