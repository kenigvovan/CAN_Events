using System.Text;
using Newtonsoft.Json.Linq;
using Vintagestory.API.MathTools;

namespace canevents.src.EventPlacesManagers.Places
{
    public partial class PlaceConfigService
    {
        public const string MobSpawnsKey = "Spawns";

        // Re-marking a block re-tags it. A tag lets a wave line spawn only at some of the points.
        public PlaceEditResult AddMobSpawn(EventPlace place, BlockPos floorPos, string tag)
        {
            if (!TryEdit(place, out var busy)) return busy;

            var arr = GetOrCreateArray(place, BehaviorKeys.Mobs, MobSpawnsKey);
            for (int i = arr.Count - 1; i >= 0; i--)
                if (arr[i] is JObject e && JsonPos.Matches(e, floorPos)) arr.RemoveAt(i);

            tag = string.IsNullOrWhiteSpace(tag) ? null : tag.Trim();
            var entry = JsonPos.Write(floorPos);
            if (tag != null) entry["Tag"] = tag;
            arr.Add(entry);
            PersistIfSaved(place);

            return PlaceEditResult.Success(string.Format(
                "Mob spawn added on '{0}' at ({1}, {2}, {3}) tag={4}. Total: {5}.",
                place.Name, floorPos.X, floorPos.Y, floorPos.Z, tag ?? "(any)", arr.Count));
        }

        public PlaceEditResult ClearMobSpawns(EventPlace place)
        {
            if (!TryEdit(place, out var busy)) return busy;

            ClearArray(place, BehaviorKeys.Mobs, MobSpawnsKey);
            return PlaceEditResult.Success(string.Format("Mob spawns cleared on '{0}'.", place.Name));
        }

        public PlaceEditResult RemoveMobSpawnAt(EventPlace place, BlockPos pos)
        {
            if (!TryEdit(place, out var busy)) return busy;

            int index = IndexAt(place, BehaviorKeys.Mobs, MobSpawnsKey, pos);
            if (index < 0 || !TryGetArray(place, BehaviorKeys.Mobs, MobSpawnsKey, out var arr))
                return PlaceEditResult.Fail(Vintagestory.API.Config.Lang.Get("canevents:wand-nothing-here"));

            arr.RemoveAt(index);
            PersistIfSaved(place);
            return PlaceEditResult.Success(string.Format("Removed mob spawn #{0} on '{1}'. Left: {2}.",
                index, place.Name, arr.Count));
        }

        public string DescribeMobSpawns(EventPlace place)
        {
            if (!TryGetArray(place, BehaviorKeys.Mobs, MobSpawnsKey, out var arr) || arr.Count == 0)
                return string.Format("No mob spawns on '{0}'.", place.Name);

            var sb = new StringBuilder();
            sb.AppendLine(string.Format("'{0}' has {1} mob spawn(s):", place.Name, arr.Count));
            foreach (var t in arr)
            {
                if (!(t is JObject e) || e["X"] == null) continue;
                sb.AppendLine(string.Format("  ({0}, {1}, {2})  tag={3}",
                    e.Value<int>("X"), e.Value<int>("Y"), e.Value<int>("Z"), e["Tag"]?.Value<string>() ?? "(any)"));
            }
            return sb.ToString().TrimEnd();
        }
    }
}
