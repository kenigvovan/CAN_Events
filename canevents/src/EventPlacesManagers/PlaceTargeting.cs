using System.Collections.Generic;
using System.Linq;
using canevents.src.EventPlacesManagers.Places;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace canevents.src.EventPlacesManagers
{
    public readonly struct PlaceTarget
    {
        public readonly EventPlace Place;
        public readonly bool FromSelection;
        // Several places contain the position; don't guess.
        public readonly List<EventPlace> Ambiguous;

        public PlaceTarget(EventPlace place, bool fromSelection, List<EventPlace> ambiguous = null)
        {
            Place = place;
            FromSelection = fromSelection;
            Ambiguous = ambiguous;
        }

        public bool Found => Place != null;
    }

    // Target for /canep and the wand: selection, else the place being built, else the one saved place
    // containing the position. Session state only.
    public sealed class PlaceTargeting
    {
        private readonly DefaultEventPlacesManager places;
        // Shared with CANEventManager.SelectedPlaceByPlayer.
        private readonly Dictionary<string, long> selected;

        public readonly Dictionary<string, EventPlace> InProgress = new Dictionary<string, EventPlace>();

        private readonly Dictionary<string, (BlockPos pos, EventPlace target)> pendingCorners
            = new Dictionary<string, (BlockPos pos, EventPlace target)>();

        public PlaceTargeting(DefaultEventPlacesManager places, Dictionary<string, long> selectedByPlayer)
        {
            this.places = places;
            this.selected = selectedByPlayer;
        }

        public bool TryGetSelection(string uid, out long id) => selected.TryGetValue(uid, out id);

        public void Select(string uid, long placeId) => selected[uid] = placeId;

        public void Unselect(string uid) => selected.Remove(uid);

        public List<EventPlace> PlacesAt(BlockPos pos)
            => places.EventPlaces.Where(p => PlaceBounds.Contains(p, pos)).OrderBy(p => p.Id).ToList();

        public PlaceTarget Resolve(string uid, BlockPos pos)
        {
            var sel = Selected(uid);
            if (sel != null) return new PlaceTarget(sel, true);

            if (InProgress.TryGetValue(uid, out var inProgress)
                && (!PlaceBounds.Has(inProgress) || PlaceBounds.Contains(inProgress, pos)))
                return new PlaceTarget(inProgress, false);

            return ByPosition(pos);
        }

        // Bounds are being redrawn, so the in-progress place wins regardless of position.
        public PlaceTarget ResolveForCorner(string uid, BlockPos pos)
        {
            var sel = Selected(uid);
            if (sel != null) return new PlaceTarget(sel, true);
            if (InProgress.TryGetValue(uid, out var inProgress)) return new PlaceTarget(inProgress, false);
            return ByPosition(pos);
        }

        public void SetPendingCorner(string uid, BlockPos pos, EventPlace target) => pendingCorners[uid] = (pos.Copy(), target);

        public bool TryGetPendingCorner(string uid, out BlockPos pos, out EventPlace target)
        {
            pos = null;
            target = null;
            if (!pendingCorners.TryGetValue(uid, out var pending)) return false;
            pos = pending.pos;
            bool alive = places.EventPlaces.Contains(pending.target)
                || (InProgress.TryGetValue(uid, out var building) && building == pending.target);
            target = alive ? pending.target : null;
            return true;
        }

        public void ClearPendingCorner(string uid) => pendingCorners.Remove(uid);

        public string NoTargetMessage(PlaceTarget target, BlockPos pos)
        {
            if (target.Ambiguous != null && target.Ambiguous.Count > 1)
                return Lang.Get("canevents:place-ambiguous", pos.X, pos.Y, pos.Z,
                    string.Join(", ", target.Ambiguous.Select(p => "#" + p.Id + " " + p.Name)));

            // Usual cause: places without bounds can't be found by position.
            int withoutBounds = places.EventPlaces.Count(p => !PlaceBounds.Has(p));
            return Lang.Get("canevents:no-target-place", pos.X, pos.Y, pos.Z)
                + (withoutBounds > 0 ? " " + Lang.Get("canevents:places-without-bounds", withoutBounds) : "");
        }

        // Warns when an edit went to the selected place while the admin stands outside it.
        public static string SelectionNote(PlaceTarget target, BlockPos pos)
        {
            if (!target.FromSelection || target.Place == null || !PlaceBounds.Has(target.Place)
                || PlaceBounds.Contains(target.Place, pos)) return "";
            return " " + Lang.Get("canevents:selected-place-outside", target.Place.Name, target.Place.Id);
        }

        private EventPlace Selected(string uid)
        {
            if (!selected.TryGetValue(uid, out long id)) return null;
            var place = places.FindById(id);
            if (place == null) selected.Remove(uid);
            return place;
        }

        private PlaceTarget ByPosition(BlockPos pos)
        {
            var here = PlacesAt(pos);
            if (here.Count == 1) return new PlaceTarget(here[0], false);
            return new PlaceTarget(null, false, here.Count > 1 ? here : null);
        }
    }
}
