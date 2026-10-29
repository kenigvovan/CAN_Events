using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers.eventBehavior.Shared;
using canevents.src.EventPlacesManagers.Places;
using canevents.src.Gui.Admin.GuiElements;
using canevents.src.Gui.Admin.Widgets;
using canevents.src.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace canevents.src.Gui.Admin.Pages.Sections
{
    public sealed partial class HungerGamesSection
    {
        private double BuildSpawns(PageBuildContext ctx, ElementBounds column, PlaceListEntryMsg place,
                                   HgSetupMsg s, double y)
        {
            var gui = ctx.Gui;
            var net = gui.Net;
            double width = column.fixedWidth - Card.Padding * 2;

            int count = place.SpawnX?.Length ?? 0;
            if (place.SpawnY == null || place.SpawnZ == null) count = 0;
            else count = Math.Min(count, Math.Min(place.SpawnY.Length, place.SpawnZ.Length));
            int cap = s?.RequiredSpawns ?? 0;

            string status = cap > 0
                ? Lang.Get("canevents:admin-hg-spawns-count-cap", count, cap)
                : Lang.Get("canevents:admin-hg-spawns-count", count);
            string fewer = cap > 0 && count > 0 && count < cap ? Lang.Get("canevents:admin-hg-spawns-fewer") : null;
            double fewerHeight = fewer != null ? ButtonRow.TextHeight(fewer, width) + 4 : 0;

            double listHeight = Math.Min(count, MaxSpawnRows) * ColumnTable.RowPitch + 10;
            double tableHeight = count > 0 ? ColumnTable.HeaderHeight + 4 + listHeight + 10 : 0;

            string addCaption = Lang.Get("canevents:admin-hg-spawn-add");
            double buttonsHeight = ButtonRow.HeightFor(width, 0, addCaption);

            double contentHeight = LineHeight + 4 + fewerHeight + buttonsHeight + tableHeight;
            var inner = Section.Begin(ctx, column, y, "hg-spawns", Lang.Get("canevents:admin-hg-spawns-title"),
                contentHeight, out double nextY);
            if (inner == null) return nextY;

            double cy = inner.fixedY;
            var statusFont = CairoFont.WhiteSmallText().WithColor(cap > 0 && count >= cap ? AdminColors.Success : AdminColors.Warning);
            ctx.Compo.AddStaticText(Card.Fit(status, statusFont, width), statusFont,
                ElementBounds.Fixed(inner.fixedX, cy, width, LineHeight), "hg-spawns-status");
            cy += LineHeight + 4;

            if (fewer != null)
            {
                ctx.Compo.AddStaticText(fewer, CairoFont.WhiteSmallText().WithColor(AdminColors.Warning),
                    ElementBounds.Fixed(inner.fixedX, cy, width, fewerHeight - 4), "hg-spawns-fewer");
                cy += fewerHeight;
            }

            var buttons = new ButtonRow(ctx.Compo, ElementBounds.Fixed(inner.fixedX, cy, width, 0), width, 0, "hg-spawns-btn");
            buttons.Add(addCaption, () =>
                gui.Act(Lang.Get("canevents:admin-hg-log-spawn-add", place.Name ?? "?"), () => net.SendAddSpawnPoint(place.Id)),
                Lang.Get("canevents:admin-hg-spawn-add-hint"));
            cy += buttonsHeight;

            if (count > 0)
            {
                var spec = new ColumnSpec(
                    new ColumnDef("#", 34),
                    new ColumnDef(Lang.Get("canevents:admin-hg-col-position"), 0),
                    new ColumnDef(Lang.Get("canevents:admin-hg-col-where"), 120),
                    new ColumnDef(null, 250));

                var rows = new List<ColumnRow>();
                for (int i = 0; i < count; i++)
                {
                    int index = i;
                    int sx = place.SpawnX[i], sy = place.SpawnY[i], sz = place.SpawnZ[i];
                    bool inside = PlaceHighlighter.PositionInsideBounds(place, sx, sz);

                    var row = new ColumnRow((i + 1).ToString(), "(" + sx + ", " + sy + ", " + sz + ")",
                        Lang.Get(inside ? "canevents:admin-hg-inside" : "canevents:admin-hg-outside"), null);
                    row.WithColor(1, inside ? AdminColors.Value : AdminColors.Danger);
                    row.WithColor(2, inside ? AdminColors.Muted : AdminColors.Danger);
                    if (!inside) row.WithCellTooltip(2, Lang.Get("canevents:admin-hg-spawn-outside-hint"));

                    row.WithButton(3, Lang.Get("canevents:admin-spawn-tp"), () =>
                        gui.Act(Lang.Get("canevents:admin-log-spawn-tp", index + 1), () => net.SendTeleportToSpawn(place.Id, index)),
                        Lang.Get("canevents:admin-spawn-tp-tip"));

                    // Move in place: remove + add would renumber the pedestal.
                    row.WithButton(3, Lang.Get("canevents:admin-hg-spawn-move"), () =>
                        gui.Act(Lang.Get("canevents:admin-hg-log-spawn-move", index + 1), () => net.SendMoveSpawnPoint(place.Id, index)),
                        Lang.Get("canevents:admin-hg-spawn-move-hint"));

                    ArmedTextButton.AddToRow(ctx, row, 3, "hg-sp-rm-" + place.Id + "-" + i,
                        Lang.Get("canevents:admin-hg-remove"), Lang.Get("canevents:admin-hg-remove-armed"),
                        Lang.Get("canevents:admin-hg-spawn-remove-hint"),
                        () => gui.Act(Lang.Get("canevents:admin-hg-log-spawn-remove", index + 1),
                            () => net.SendRemoveSpawnPoint(place.Id, index)));
                    rows.Add(row);
                }

                ColumnTable.Add(ctx, ElementBounds.Fixed(inner.fixedX, cy, width, 0), spec, rows, listHeight,
                    new ScrollableListOptions { Key = "hg-spawn-list" });
            }
            return nextY;
        }
    }
}
