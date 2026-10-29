using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace canevents.src.EventPlacesManagers.Places
{
    public partial class PlaceConfigService
    {
        // Where WaitingRoomEB holds the line-up before the start. May lie outside the arena bounds,
        // so target the place with /canep select first.
        public PlaceEditResult SetWaitingRoom(EventPlace place, BlockPos floorPos)
        {
            if (!TryEdit(place, out var busy)) return busy;

            JsonPos.WriteInto(place.GetOrCreateBehaviorData(BehaviorKeys.WaitingRoom), floorPos);
            PersistIfSaved(place);
            return PlaceEditResult.Success(Lang.Get("canevents:place-waitroom-set",
                place.Name, floorPos.X, floorPos.Y, floorPos.Z));
        }

        public static bool TryGetWaitingRoom(EventPlace place, out BlockPos floorPos)
        {
            floorPos = null;
            return place != null
                   && place.TryGetBehaviorData(BehaviorKeys.WaitingRoom, out var data)
                   && JsonPos.TryRead(data, out floorPos);
        }
    }
}
