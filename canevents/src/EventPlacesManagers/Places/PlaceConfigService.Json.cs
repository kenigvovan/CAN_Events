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
        // JSON-array helpers shared by several modes; see PlaceConfigService.cs for the shared rules.

        private static int IndexAt(EventPlace place, string key, string field, BlockPos pos)
        {
            if (!TryGetArray(place, key, field, out var arr)) return -1;
            for (int i = 0; i < arr.Count; i++)
                if (arr[i] is JObject e && e["X"] != null && JsonPos.Matches(e, pos)) return i;
            return -1;
        }

        private static JArray GetOrCreateArray(EventPlace place, string behaviorKey, string field)
        {
            var data = place.GetOrCreateBehaviorData(behaviorKey);
            var arr = data[field] as JArray;
            if (arr == null) { arr = new JArray(); data[field] = arr; }
            return arr;
        }

        // A list stored in a mode's data, when the place has one.
        private static bool TryGetArray(EventPlace place, string behaviorKey, string field, out JArray arr)
        {
            arr = null;
            if (place == null || !place.TryGetBehaviorData(behaviorKey, out var data)) return false;
            arr = data[field] as JArray;
            return arr != null;
        }

        // Same, with index in range.
        private static bool TryGetArray(EventPlace place, string behaviorKey, string field, int index, out JArray arr)
            => TryGetArray(place, behaviorKey, field, out arr) && index >= 0 && index < arr.Count;

        // Empties a mode's list and saves; nothing to do when the mode has no data.
        private void ClearArray(EventPlace place, string behaviorKey, string field)
        {
            if (!place.TryGetBehaviorData(behaviorKey, out var data)) return;
            data[field] = new JArray();
            PersistIfSaved(place);
        }
    }
}
