using System;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace canevents.src.EventPlacesManagers.Places
{
    public partial class PlaceConfigService
    {
        // Post [0] is team 1. Skipped slots stay empty so later teams keep their index.
        public PlaceEditResult SetCtfPost(EventPlace place, BlockPos floorPos, int team)
        {
            if (!TryEdit(place, out var busy)) return busy;
            if (team < 1 || team > MaxCtfTeams) return PlaceEditResult.Fail(Lang.Get("canevents:ctf-team-min"));

            var arr = GetOrCreateArray(place, BehaviorKeys.CaptureTheFlag, "Posts");
            while (arr.Count < team) arr.Add(new JObject());

            arr[team - 1] = JsonPos.Write(floorPos);
            PersistIfSaved(place);

            return PlaceEditResult.Success(string.Format(
                "CTF post for team {0} set on '{1}' at ({2}, {3}, {4}). Posts: {5}.",
                team, place.Name, floorPos.X, floorPos.Y, floorPos.Z, PlaceSetupCheck.CtfPostCount(place)));
        }

        public PlaceEditResult ClearCtfPosts(EventPlace place)
        {
            if (!TryEdit(place, out var busy)) return busy;

            ClearArray(place, BehaviorKeys.CaptureTheFlag, "Posts");
            return PlaceEditResult.Success(string.Format("CTF posts cleared on '{0}'.", place.Name));
        }

        public string DescribeCtfPosts(EventPlace place)
        {
            if (!TryGetArray(place, BehaviorKeys.CaptureTheFlag, "Posts", out var arr) || arr.Count == 0)
                return string.Format("No CTF posts on '{0}'.", place.Name);

            var sb = new StringBuilder();
            sb.AppendLine(string.Format("'{0}' has {1} CTF post(s):", place.Name, arr.Count));
            for (int i = 0; i < arr.Count; i++)
            {
                var e = arr[i] as JObject;
                if (e == null || e["X"] == null)
                    sb.AppendLine(string.Format("  team {0}: (not set)", i + 1));
                else
                    sb.AppendLine(string.Format("  team {0}: ({1}, {2}, {3})",
                        i + 1, e.Value<int>("X"), e.Value<int>("Y"), e.Value<int>("Z")));
            }
            return sb.ToString().TrimEnd();
        }

        // First team without a post. A block that already has a post keeps its team.
        public PlaceEditResult SetNextCtfPost(EventPlace place, BlockPos floorPos)
        {
            int existing = IndexAt(place, BehaviorKeys.CaptureTheFlag, "Posts", floorPos);
            if (existing >= 0) return SetCtfPost(place, floorPos, existing + 1);

            int team = 1;
            if (TryGetArray(place, BehaviorKeys.CaptureTheFlag, "Posts", out var arr))
            {
                team = arr.Count + 1;
                for (int i = 0; i < arr.Count; i++)
                    if (!JsonPos.TryRead(arr[i], out _)) { team = i + 1; break; }
            }
            return SetCtfPost(place, floorPos, team);
        }

        // Slot is left empty so later teams keep their number.
        public PlaceEditResult RemoveCtfPostAt(EventPlace place, BlockPos pos)
        {
            if (!TryEdit(place, out var busy)) return busy;
            int index = IndexAt(place, BehaviorKeys.CaptureTheFlag, "Posts", pos);
            if (index < 0) return PlaceEditResult.Fail(Lang.Get("canevents:wand-nothing-here"));

            var arr = (JArray)place.BehaviorData[BehaviorKeys.CaptureTheFlag]["Posts"];
            arr[index] = new JObject();
            PersistIfSaved(place);
            return PlaceEditResult.Success(string.Format("CTF post of team {0} removed on '{1}'.", index + 1, place.Name));
        }
    }
}
