using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using Vintagestory.API.MathTools;

namespace canevents.src.EventPlacesManagers.Places
{
    // Admin panel readout of each mode's BehaviorData. Kept out of the network layer.
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
                // Empty slots listed too: that team has no post.
                for (int i = 0; i < postArr.Count; i++)
                {
                    lines.Add(JsonPos.TryRead(postArr[i], out var post)
                        ? string.Format("ctf post {0} @ {1},{2},{3}", i + 1, post.X, post.Y, post.Z)
                        : string.Format("ctf post {0}: NOT SET", i + 1));
                }
            }

            if (place.TryGetBehaviorData(BehaviorKeys.MurderMystery, out var mm)
                && mm["PickupSpots"] is JArray pickupArr)
            {
                for (int i = 0; i < pickupArr.Count; i++)
                {
                    lines.Add(JsonPos.TryRead(pickupArr[i], out var spot)
                        ? string.Format("mm pickup {0} @ {1},{2},{3}", i, spot.X, spot.Y, spot.Z)
                        : string.Format("mm pickup {0}: NOT SET", i));
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

        // Malformed entries keep their slot as (0,0,0); the panel removes by index.
        public static List<BlockPos> PackMurderMysteryPickups(EventPlace place)
        {
            var spots = new List<BlockPos>();
            if (!place.TryGetBehaviorData(BehaviorKeys.MurderMystery, out var data)
                || !(data["PickupSpots"] is JArray arr)) return spots;

            foreach (var token in arr)
                spots.Add(JsonPos.TryRead(token, out var pos) ? pos : new BlockPos(0, 0, 0, 0));
            return spots;
        }

        // "x,y,z,dim,index,finish". Malformed entries skipped; the panel addresses by position.
        public static string[] PackParkourCheckpoints(EventPlace place)
        {
            var packed = new List<string>();
            if (!place.TryGetBehaviorData(BehaviorKeys.Parkour, out var data)
                || !(data["Checkpoints"] is JArray arr)) return packed.ToArray();

            foreach (var token in arr)
            {
                if (!JsonPos.TryRead(token, out var pos)) continue;
                var entry = (JObject)token;
                packed.Add(string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3},{4},{5}",
                    pos.X, pos.Y, pos.Z, pos.dimension,
                    entry.Value<int>("Index"), entry.Value<bool>("IsFinish") ? 1 : 0));
            }
            return packed.ToArray();
        }

        public static int ParkourLives(EventPlace place)
            => place.TryGetBehaviorData(BehaviorKeys.Parkour, out var data) && data["Lives"] != null
                ? data.Value<int>("Lives")
                : 0;

        // Without a stored zone the bounds centre is used.
        public static bool HasHgZone(EventPlace place)
            => place.TryGetBehaviorData(BehaviorKeys.HungerGames, out var data) && JsonPos.TryRead(data, out _);

        public static int SpleefPlatformBlocks(EventPlace place)
            => place.TryGetBehaviorData(BehaviorKeys.Spleef, out var data)
                ? (data["PlatformX"] as JArray)?.Count ?? 0
                : 0;

        // Team order, "" for a team without a post. Strings since an all-default int array can arrive short.
        public static string[] PackCtfPosts(EventPlace place)
        {
            var packed = new List<string>();
            if (!place.TryGetBehaviorData(BehaviorKeys.CaptureTheFlag, out var data)
                || !(data["Posts"] is JArray arr)) return packed.ToArray();

            foreach (var token in arr)
                packed.Add(JsonPos.TryRead(token, out var pos)
                    ? string.Format(CultureInfo.InvariantCulture, "{0},{1},{2}", pos.X, pos.Y, pos.Z)
                    : "");
            return packed.ToArray();
        }

        public static int Dimension(EventPlace place)
        {
            if (place.BoundsMin != null) return place.BoundsMin.dimension;
            var spawns = place.spawnPositions;
            return spawns != null && spawns.Count > 0 && spawns[0] != null ? spawns[0].dimension : 0;
        }

        // "x,y,z,r,h" for the hill overlay.
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
