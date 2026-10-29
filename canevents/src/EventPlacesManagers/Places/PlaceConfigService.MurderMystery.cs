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
        // Murder Mystery place edits; see PlaceConfigService.cs for the shared rules.

        // Murder Mystery

        // Re-marking the same block is a no-op: two spots on one block would let one corner hold two
        // tokens, which the minimum-distance rule exists to prevent.
        public PlaceEditResult AddMurderMysteryPickup(EventPlace place, BlockPos floorPos)
        {
            if (!TryEdit(place, out var busy)) return busy;

            var arr = GetOrCreateArray(place, BehaviorKeys.MurderMystery, "PickupSpots");
            for (int i = 0; i < arr.Count; i++)
                if (arr[i] is JObject e && JsonPos.Matches(e, floorPos))
                    return PlaceEditResult.Fail(Lang.Get("canevents:mm-pickup-duplicate"));

            arr.Add(JsonPos.Write(floorPos));
            PersistIfSaved(place);

            return PlaceEditResult.Success(string.Format(
                "MM pickup spot added on '{0}' at ({1}, {2}, {3}). Total: {4}.",
                place.Name, floorPos.X, floorPos.Y, floorPos.Z, arr.Count));
        }

        // Index in the stored array, the one the panel renders.
        public PlaceEditResult RemoveMurderMysteryPickup(EventPlace place, int index)
        {
            if (!TryEdit(place, out var busy)) return busy;
            if (!TryGetPickupArray(place, index, out var arr))
                return PlaceEditResult.Fail(Lang.Get("canevents:mm-pickup-invalid-index"));

            arr.RemoveAt(index);
            PersistIfSaved(place);
            return PlaceEditResult.Success(string.Format("Removed MM pickup spot #{0} on '{1}'. Left: {2}.",
                index, place.Name, arr.Count));
        }

        public PlaceEditResult ClearMurderMysteryPickups(EventPlace place)
        {
            if (!TryEdit(place, out var busy)) return busy;

            ClearArray(place, BehaviorKeys.MurderMystery, "PickupSpots");
            return PlaceEditResult.Success(string.Format("MM pickup spots cleared on '{0}'.", place.Name));
        }

        // Read-only, so it works on an occupied place too.
        public string DescribeMurderMysteryPickups(EventPlace place)
        {
            if (!TryGetArray(place, BehaviorKeys.MurderMystery, "PickupSpots", out var arr) || arr.Count == 0)
                return string.Format("No pickup spots on '{0}'.", place.Name);

            var sb = new StringBuilder();
            sb.AppendLine(string.Format("'{0}' has {1} pickup spot(s):", place.Name, arr.Count));
            for (int i = 0; i < arr.Count; i++)
            {
                if (!JsonPos.TryRead(arr[i], out var pos)) continue;
                sb.AppendLine(string.Format("  #{0}  ({1}, {2}, {3})", i, pos.X, pos.Y, pos.Z));
            }
            return sb.ToString().TrimEnd();
        }

        public PlaceEditResult RemoveMurderMysteryPickupAt(EventPlace place, BlockPos pos)
        {
            int index = IndexAt(place, BehaviorKeys.MurderMystery, "PickupSpots", pos);
            return index < 0 ? PlaceEditResult.Fail(Lang.Get("canevents:wand-nothing-here")) : RemoveMurderMysteryPickup(place, index);
        }

        private static bool TryGetPickupArray(EventPlace place, int index, out JArray arr)
            => TryGetArray(place, BehaviorKeys.MurderMystery, "PickupSpots", index, out arr);
    }
}
