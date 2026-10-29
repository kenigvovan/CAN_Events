using System.Linq;
using canevents.src.Gui.Admin.Widgets;
using canevents.src.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace canevents.src.Gui.Admin.Pages
{
    // Waiting room (battle) and open-match pads (TDM) on the Modes sub-tab, for places whose tags a
    // loaded event of that kind asks for. The zone lists themselves are the shared zone card.
    public sealed partial class ArenasPage
    {
        internal AdminEventListEntryMsg WaitingRoomEventFor(PlaceListEntryMsg place)
            => EventFor(place, e => e.NeedsWaitingRoom);

        internal AdminEventListEntryMsg OpenMatchEventFor(PlaceListEntryMsg place)
            => EventFor(place, e => e.IsOpenMatch);

        private AdminEventListEntryMsg EventFor(PlaceListEntryMsg place, System.Func<AdminEventListEntryMsg, bool> kind)
            => Net.AdminEventList.Value?.Events?.FirstOrDefault(e => e != null && kind(e)
                && (e.EventPlaceTypes ?? new string[0]).All(t => PlaceHighlighter.HasType(place, t)));

        private double BuildWaitingRoom(PageBuildContext ctx, ElementBounds column, PlaceListEntryMsg place, double y,
                                        AdminEventListEntryMsg ev)
        {
            double width = column.fixedWidth - Card.Padding * 2;
            long id = place.Id;
            string name = place.Name ?? "?";
            string roomCap = Lang.Get("canevents:admin-wizard-waitroom-here");
            string hint = Lang.Get("canevents:admin-wizard-waitroom-hint");

            double content = LineHeight + 4 + ButtonRow.MultiLineHeight(hint, width) + 4 + ButtonRow.HeightFor(width, 0, roomCap);
            var inner = Section.Begin(ctx, column, y, "ar-waitroom",
                Lang.Get("canevents:admin-arenas-waitroom-title", ev.Code), content, out double nextY);

            if (inner != null)
            {
                double cy = inner.fixedY;
                bool hasRoom = !string.IsNullOrEmpty(place.WaitingRoom);
                Line(ctx, inner.fixedX, cy, width, hasRoom
                        ? Lang.Get("canevents:admin-wizard-waitroom-set", place.WaitingRoom)
                        : Lang.Get("canevents:admin-wizard-waitroom-todo"),
                    hasRoom ? AdminColors.Success : AdminColors.Danger);
                cy += LineHeight + 4;
                Wrapped(ctx, inner.fixedX, cy, width, hint, AdminFonts.Hint);
                cy += ButtonRow.MultiLineHeight(hint, width) + 4;

                var roomRow = new ButtonRow(ctx.Compo, ElementBounds.Fixed(inner.fixedX, cy, width, 0), width, 0, "ar-wr-room");
                roomRow.Add(roomCap, () => Act(Lang.Get("canevents:admin-wizard-log-waitroom", name), () => Net.SendSetWaitingRoom(id)));
            }

            return BuildZoneCard(ctx, column, nextY, SideZonesSpec(ev, place));
        }
    }
}
