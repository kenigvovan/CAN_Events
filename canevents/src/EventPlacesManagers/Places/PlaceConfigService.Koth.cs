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
        // King of the Hill place edits; see PlaceConfigService.cs for the shared rules.

        // King of the Hill

        // Height <= 0 means unset and falls back to the default of 5: an omitted OptionalFloat
        // parser yields 0, not null.
        public PlaceEditResult SetKothHill(EventPlace place, BlockPos floorPos, float radius, float height)
        {
            if (!TryEdit(place, out var busy)) return busy;
            if (radius <= 0f) return PlaceEditResult.Fail(Lang.Get("canevents:koth-radius-min"));
            if (height <= 0f) height = 5f;

            var data = place.GetOrCreateBehaviorData(BehaviorKeys.KingOfTheHill);
            JsonPos.WriteInto(data, floorPos);
            data["Radius"] = radius;
            data["Height"] = height;
            PersistIfSaved(place);

            return PlaceEditResult.Success(string.Format(
                "KOTH hill set on '{0}' at ({1}, {2}, {3}) r={4} h={5}.",
                place.Name, floorPos.X, floorPos.Y, floorPos.Z, radius, height));
        }
    }
}
