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
        // Replaces whatever was on that block, and the previous finish.
        public PlaceEditResult AddParkourCheckpoint(EventPlace place, BlockPos floorPos, bool isFinish)
        {
            if (!TryEdit(place, out var busy)) return busy;

            var arr = GetOrCreateArray(place, BehaviorKeys.Parkour, "Checkpoints");

            // Gaps are fine, a match renumbers the course in order.
            int index = 0;
            if (!isFinish)
                foreach (var t in arr)
                    if (t is JObject e && !e.Value<bool>("IsFinish"))
                        index = Math.Max(index, e.Value<int>("Index") + 1);

            WriteCheckpoint(arr, floorPos, index, isFinish);
            PersistIfSaved(place);

            return PlaceEditResult.Success(string.Format("{0} marked on '{1}' at ({2}, {3}, {4}).",
                isFinish ? "Finish" : "Checkpoint " + index, place.Name, floorPos.X, floorPos.Y, floorPos.Z));
        }

        public PlaceEditResult RemoveParkourCheckpoint(EventPlace place, BlockPos pos)
        {
            if (!TryEdit(place, out var busy)) return busy;
            if (!TryGetCheckpointArray(place, out var arr))
                return PlaceEditResult.Fail(Lang.Get("canevents:parkour-no-checkpoint-at", pos.X, pos.Y, pos.Z));

            int removed = 0;
            for (int i = arr.Count - 1; i >= 0; i--)
            {
                if (arr[i] is JObject e && JsonPos.Matches(e, pos))
                {
                    arr.RemoveAt(i);
                    removed++;
                }
            }
            if (removed == 0)
                return PlaceEditResult.Fail(Lang.Get("canevents:parkour-no-checkpoint-at", pos.X, pos.Y, pos.Z));

            PersistIfSaved(place);
            return PlaceEditResult.Success(string.Format("Removed the checkpoint at ({0}, {1}, {2}) on '{3}'.",
                pos.X, pos.Y, pos.Z, place.Name));
        }

        // A checkpoint already holding that index is replaced; duplicates would make the order undefined.
        public PlaceEditResult SetParkourCheckpointIndex(EventPlace place, BlockPos pos, int index)
        {
            if (!TryEdit(place, out var busy)) return busy;
            if (index < 0) return PlaceEditResult.Fail(Lang.Get("canevents:parkour-index-min"));

            JObject entry = null;
            if (TryGetCheckpointArray(place, out var arr))
                foreach (var t in arr)
                    if (t is JObject e && JsonPos.Matches(e, pos)) { entry = e; break; }

            if (entry == null)
                return PlaceEditResult.Fail(Lang.Get("canevents:parkour-no-checkpoint-at", pos.X, pos.Y, pos.Z));
            if (entry.Value<bool>("IsFinish"))
                return PlaceEditResult.Fail(Lang.Get("canevents:parkour-finish-no-index"));

            WriteCheckpoint(arr, pos, index, isFinish: false);
            PersistIfSaved(place);

            return PlaceEditResult.Success(string.Format("Checkpoint at ({0}, {1}, {2}) on '{3}' is now index {4}.",
                pos.X, pos.Y, pos.Z, place.Name, index));
        }

        public PlaceEditResult SetParkourCheckpoint(EventPlace place, BlockPos floorPos, int index)
        {
            if (!TryEdit(place, out var busy)) return busy;
            if (index < 0) return PlaceEditResult.Fail(Lang.Get("canevents:parkour-index-min"));

            WriteCheckpoint(GetOrCreateArray(place, BehaviorKeys.Parkour, "Checkpoints"), floorPos, index, isFinish: false);
            PersistIfSaved(place);

            return PlaceEditResult.Success(string.Format("Checkpoint {0} marked on '{1}' at ({2}, {3}, {4}).",
                index, place.Name, floorPos.X, floorPos.Y, floorPos.Z));
        }

        public string DescribeParkourCheckpoints(EventPlace place)
        {
            var entries = new System.Collections.Generic.List<(int index, bool isFinish, BlockPos pos)>();
            if (TryGetCheckpointArray(place, out var arr))
                foreach (var t in arr)
                    if (t is JObject e && JsonPos.TryRead(e, out var pos))
                        entries.Add((e.Value<int>("Index"), e.Value<bool>("IsFinish"), pos));

            string fall = place.TryGetBehaviorData(BehaviorKeys.Parkour, out var pd) && pd["FallY"] != null
                ? "\n  fall height: Y < " + pd.Value<int>("FallY")
                : "\n  fall height: default (15 below the lowest spawn)";
            if (entries.Count == 0) return string.Format("No checkpoints in '{0}'.", place.Name) + fall;

            entries.Sort((a, b) => a.isFinish != b.isFinish ? (a.isFinish ? 1 : -1) : a.index.CompareTo(b.index));
            var sb = new StringBuilder();
            sb.AppendLine(string.Format("Place '{0}' has {1} checkpoint(s):", place.Name, entries.Count));
            foreach (var f in entries)
                sb.AppendLine(string.Format("  {0} - ({1}, {2}, {3})",
                    f.isFinish ? "FINISH" : ("idx=" + f.index), f.pos.X, f.pos.Y, f.pos.Z));
            return sb.ToString().TrimEnd() + fall;
        }

        public PlaceEditResult SetParkourLives(EventPlace place, int lives)
        {
            if (!TryEdit(place, out var busy)) return busy;
            if (lives < 1) return PlaceEditResult.Fail(Lang.Get("canevents:parkour-lives-min"));

            place.GetOrCreateBehaviorData(BehaviorKeys.Parkour)["Lives"] = lives;
            PersistIfSaved(place);
            return PlaceEditResult.Success(string.Format("Lives for '{0}' set to {1}.", place.Name, lives));
        }

        // Null clears it; ParkourEB then uses 15 blocks under the lowest spawn.
        public PlaceEditResult SetParkourFallY(EventPlace place, int? fallY)
        {
            if (!TryEdit(place, out var busy)) return busy;

            var data = place.GetOrCreateBehaviorData(BehaviorKeys.Parkour);
            if (fallY.HasValue) data["FallY"] = fallY.Value;
            else data.Remove("FallY");
            PersistIfSaved(place);
            return PlaceEditResult.Success(fallY.HasValue
                ? Lang.Get("canevents:parkour-fally-set", place.Name, fallY.Value)
                : Lang.Get("canevents:parkour-fally-cleared", place.Name));
        }

        // One entry per block and per (index, finish) key.
        private static void WriteCheckpoint(JArray arr, BlockPos pos, int index, bool isFinish)
        {
            for (int i = arr.Count - 1; i >= 0; i--)
            {
                if (!(arr[i] is JObject e)) continue;
                bool sameKey = e.Value<bool>("IsFinish") == isFinish
                    && (isFinish || e.Value<int>("Index") == index);
                if (sameKey || JsonPos.Matches(e, pos)) arr.RemoveAt(i);
            }

            var entry = JsonPos.Write(pos);
            entry["Index"] = index;
            entry["IsFinish"] = isFinish;
            arr.Add(entry);
        }

        private static bool TryGetCheckpointArray(EventPlace place, out JArray arr)
            => TryGetArray(place, BehaviorKeys.Parkour, "Checkpoints", out arr);
    }
}
