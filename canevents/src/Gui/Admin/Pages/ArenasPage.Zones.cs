using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.Gui.Admin.GuiElements;
using canevents.src.Gui.Admin.Widgets;
using canevents.src.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using TeamPalette = canevents.src.EventManagers.eventBehavior.Shared.TeamPalette;

namespace canevents.src.Gui.Admin.Pages
{
    // Floor zones (JoinPads) of one event: the list with each zone's box, then marking a new box and
    // making it a zone, then editing an existing one. Side zones of a waiting room and the join/exit
    // pads of an open match use the same card; the wizard borrows it too.
    public sealed partial class ArenasPage
    {
        internal sealed class ZoneCardSpec
        {
            public string Key;
            public string Title;
            public string EventCode;
            // "side" or "join"
            public string Action;
            public int Teams;
            // Side zones belong to this place's waiting room.
            public long PlaceId;
            // Open match: exit zones are listed and made here too.
            public bool WithExits;
            public string Hint;
        }

        internal double BuildZoneCard(PageBuildContext ctx, ElementBounds column, double y, ZoneCardSpec s)
        {
            double width = column.fixedWidth - Card.Padding * 2;
            string zoneKey = s.Action == "side" ? "canevents:admin-wizard-side-zone" : "canevents:admin-arenas-zone-join";

            var cornerCaps = new[]
            {
                Lang.Get("canevents:admin-wizard-corner", 1), Lang.Get("canevents:admin-wizard-corner", 2),
                Lang.Get("canevents:admin-wizard-sides-show")
            };
            var makeCaps = Enumerable.Range(0, s.Teams).Select(t => Lang.Get(zoneKey, TeamPalette.NameOf(t)))
                .Concat(s.WithExits ? new[] { Lang.Get("canevents:admin-arenas-zone-exit") } : new string[0])
                .ToArray();
            var editCaps = new[]
            {
                Lang.Get("canevents:admin-wizard-zone-pick"), Lang.Get("canevents:admin-wizard-zone-reshape"),
                Lang.Get("canevents:admin-wizard-zone-delete")
            };
            var teamCaps = Enumerable.Range(0, s.Teams)
                .Select(t => Lang.Get("canevents:admin-wizard-zone-team", TeamPalette.NameOf(t))).ToArray();
            string editHint = Lang.Get("canevents:admin-wizard-zone-edit-hint");

            var hl = Highlighter;
            bool haveTarget = hl.TryGetMarkTarget(out var target, out bool fromLook);
            string targetLine = haveTarget
                ? Lang.Get(fromLook ? "canevents:admin-arenas-mark-target-look" : "canevents:admin-arenas-mark-target-feet",
                    target.X, target.Y, target.Z)
                : Lang.Get("canevents:admin-arenas-mark-target-none");

            var zones = ZonesOf(s);
            int pickedId = Net.PadList.Value?.PickedId ?? 0;
            double listHeight = Math.Min(Math.Max(zones.Length, 1), 6) * ColumnTable.RowPitch + 10;
            double listBlock = zones.Length == 0
                ? LineHeight + 4
                : LineHeight + 4 + ColumnTable.HeaderHeight + 4 + listHeight + 10;

            double content = listBlock
                + ButtonRow.MultiLineHeight(s.Hint, width) + 4
                + LineHeight + 2
                + LineHeight + ToggleRow
                + ButtonRow.HeightFor(width, 0, cornerCaps) + 4
                + ButtonRow.HeightFor(width, 0, makeCaps) + 8
                + ButtonRow.MultiLineHeight(editHint, width) + 4
                + ButtonRow.HeightFor(width, 0, editCaps) + 4
                + ButtonRow.HeightFor(width, 0, teamCaps);

            var inner = Section.Begin(ctx, column, y, s.Key, s.Title, content, out double nextY);
            if (inner == null) return nextY;

            double x = inner.fixedX;
            double cy = inner.fixedY;

            // What is there already: which team, where the box runs.
            if (zones.Length == 0)
            {
                Line(ctx, x, cy, width, Lang.Get("canevents:admin-arenas-zones-none"), AdminColors.Danger);
                cy += LineHeight + 4;
            }
            else
            {
                Line(ctx, x, cy, width, Lang.Get("canevents:admin-arenas-zones-count", zones.Length), AdminColors.Label);
                cy += LineHeight + 4;
                AddZoneTable(ctx, x, cy, width, listHeight, zones, pickedId, s.Key);
                cy += ColumnTable.HeaderHeight + 4 + listHeight + 10;
            }

            Wrapped(ctx, x, cy, width, s.Hint, AdminFonts.Hint);
            cy += ButtonRow.MultiLineHeight(s.Hint, width) + 4;

            Line(ctx, x, cy, width, Lang.Get("canevents:admin-arenas-zone-corners",
                    hl.HasMark1 ? hl.Mark1X + ", " + hl.Mark1Y + ", " + hl.Mark1Z : "-",
                    hl.HasMark2 ? hl.Mark2X + ", " + hl.Mark2Y + ", " + hl.Mark2Z : "-"),
                hl.HasMark1 && hl.HasMark2 ? AdminColors.Success : AdminColors.Muted);
            cy += LineHeight + 2;

            // Same switch as the bounds card: it carries over, so show where the next corner lands.
            Line(ctx, x, cy, width, targetLine, haveTarget ? AdminColors.Label : AdminColors.Danger);
            cy += LineHeight;
            AddToggle(ctx, x, cy, width, s.Key + "-mark-feet", hl.MarkAtFeet, on =>
            {
                hl.MarkAtFeet = on;
                Rebuild();
            }, Lang.Get("canevents:admin-arenas-mark-at-feet"), Lang.Get("canevents:admin-arenas-mark-at-feet-tip"));
            cy += ToggleRow;

            // Corners are the panel's own marks: aim at panel open (one corner per aim) or the block underfoot,
            // previewed as an orange box like arena bounds.
            var cornerRow = new ButtonRow(ctx.Compo, ElementBounds.Fixed(x, cy, width, 0), width, 0, s.Key + "-corner");
            cornerRow.Add(cornerCaps[0], () => { Highlighter.TakeMark(1); Rebuild(); });
            cornerRow.Add(cornerCaps[1], () => { Highlighter.TakeMark(2); Rebuild(); });
            cornerRow.Add(cornerCaps[2], () => Net.SendPadEdit("show"));
            cy += ButtonRow.HeightFor(width, 0, cornerCaps) + 4;

            var makeRow = new ButtonRow(ctx.Compo, ElementBounds.Fixed(x, cy, width, 0), width, 0, s.Key + "-make");
            for (int t = 0; t < s.Teams; t++)
            {
                int team = t;
                string cap = makeCaps[t];
                makeRow.Add(cap, () => SendMarkedBox(s.Action, s.EventCode, team, s.PlaceId,
                    Lang.Get("canevents:admin-arenas-log-zone", cap)));
            }
            if (s.WithExits)
            {
                string exitCap = makeCaps[s.Teams];
                makeRow.Add(exitCap, () => SendMarkedBox("leave", null, 0, 0, Lang.Get("canevents:admin-arenas-log-zone", exitCap)));
            }
            cy += ButtonRow.HeightFor(width, 0, makeCaps) + 8;

            Wrapped(ctx, x, cy, width, editHint, AdminFonts.Hint);
            cy += ButtonRow.MultiLineHeight(editHint, width) + 4;

            var editRow = new ButtonRow(ctx.Compo, ElementBounds.Fixed(x, cy, width, 0), width, 0, s.Key + "-edit");
            editRow.Add(editCaps[0], () => Net.SendPadEdit("pick"));
            editRow.Add(editCaps[1], () => SendMarkedBox("reshape", null, 0, 0, null));
            editRow.Add(editCaps[2], () => Net.SendPadEdit("delete"));
            cy += ButtonRow.HeightFor(width, 0, editCaps) + 4;

            var teamRow = new ButtonRow(ctx.Compo, ElementBounds.Fixed(x, cy, width, 0), width, 0, s.Key + "-team");
            for (int t = 0; t < s.Teams; t++)
            {
                int team = t;
                teamRow.Add(teamCaps[t], () => Net.SendPadEdit("team", null, team));
            }

            return nextY;
        }

        private PadEntryMsg[] ZonesOf(ZoneCardSpec s)
            => (Net.PadList.Value?.Pads ?? new PadEntryMsg[0])
                .Where(p => p != null
                    && ((p.Action == s.Action && (s.Action == "side" && p.PlaceId != 0
                            // Side zones belong to the room, whatever event they were made from.
                            ? p.PlaceId == s.PlaceId
                            : string.Equals(p.EventCode, s.EventCode, StringComparison.OrdinalIgnoreCase)))
                        || (s.WithExits && p.Action == "leave")))
                .OrderBy(p => p.Action == "leave" ? 1 : 0).ThenBy(p => p.Team).ThenBy(p => p.Id)
                .ToArray();

        private void AddZoneTable(PageBuildContext ctx, double x, double y, double width, double listHeight,
                                  PadEntryMsg[] zones, int pickedId, string key)
        {
            var spec = new ColumnSpec(
                new ColumnDef("#", 40),
                new ColumnDef(Lang.Get("canevents:admin-arenas-zones-col-team"), 110),
                new ColumnDef(Lang.Get("canevents:admin-arenas-zones-col-box"), 0),
                new ColumnDef(Lang.Get("canevents:admin-arenas-zones-col-size"), 70),
                new ColumnDef(null, 230));

            var rows = new List<ColumnRow>();
            foreach (var z in zones)
            {
                var zone = z;
                bool exit = zone.Action == "leave";
                var c = TeamPalette.Rgba(exit ? -1 : zone.Team);
                string box = zone.MinX + ", " + zone.MinY + ", " + zone.MinZ + "  →  " + zone.MaxX + ", " + zone.MinY + ", " + zone.MaxZ;
                string size = (zone.MaxX - zone.MinX + 1) + "×" + (zone.MaxZ - zone.MinZ + 1);
                string who = exit ? Lang.Get("canevents:admin-arenas-zone-exit-label") : TeamPalette.NameOf(zone.Team);
                var row = new ColumnRow("#" + zone.Id + (zone.Id == pickedId ? " *" : ""), who, box, size, null)
                    .WithColor(0, zone.Id == pickedId ? AdminColors.Success : AdminColors.Value)
                    .WithColor(1, new double[] { c.X, c.Y, c.Z, 1 })
                    .WithColor(2, AdminColors.Muted)
                    .WithColor(3, AdminColors.Label);
                row.WithButton(4, Lang.Get("canevents:admin-arenas-zones-pick"), () => Net.SendPadEdit("pickid", padId: zone.Id),
                    Lang.Get("canevents:admin-arenas-zones-pick-tip"));
                row.WithButton(4, Lang.Get("canevents:admin-spawn-tp"), () => Net.SendPadEdit("tp", padId: zone.Id));
                ArmedTextButton.AddToRow(ctx, row, 4, key + "-rm-" + zone.Id,
                    Lang.Get("canevents:admin-arenas-remove"), Lang.Get("canevents:admin-arenas-remove-armed"), null,
                    () => Net.SendPadEdit("deleteid", padId: zone.Id));
                rows.Add(row);
            }

            ColumnTable.Add(ctx, ElementBounds.Fixed(x, y, width, 0), spec, rows, listHeight,
                new ScrollableListOptions { Key = key + "-list" });
        }

        // Both marks set -> send the box and drop the marks, so the next zone starts clean.
        internal bool SendMarkedBox(string op, string code, int team, long placeId, string logLine)
        {
            var hl = Highlighter;
            if (!hl.HasMark1 || !hl.HasMark2)
            {
                Gui.SetStatus(false, Lang.Get("canevents:pad-need-corners"));
                return false;
            }
            int x1 = hl.Mark1X, y1 = hl.Mark1Y, z1 = hl.Mark1Z, x2 = hl.Mark2X, y2 = hl.Mark2Y, z2 = hl.Mark2Z;
            void Send() => Net.SendPadBox(op, code, team, placeId, x1, y1, z1, x2, y2, z2);
            if (logLine != null) Act(logLine, Send);
            else Send();
            hl.ClearMarks();
            Rebuild();
            return true;
        }

        // Specs for the two kinds of event that have zones; null when the event has none.
        internal static ZoneCardSpec SideZonesSpec(AdminEventListEntryMsg ev, PlaceListEntryMsg place)
            => new ZoneCardSpec
            {
                Key = "ar-sidezones",
                Title = Lang.Get("canevents:admin-arenas-sidezones-title", ev.Code),
                EventCode = ev.Code,
                Action = "side",
                Teams = Math.Max(2, ev.Teams),
                PlaceId = place.Id,
                Hint = Lang.Get("canevents:admin-wizard-sides-hint")
            };

        internal static ZoneCardSpec OpenPadsSpec(AdminEventListEntryMsg ev)
            => new ZoneCardSpec
            {
                Key = "ar-openpads",
                Title = Lang.Get("canevents:admin-arenas-openpads-title", ev.Code),
                EventCode = ev.Code,
                Action = "join",
                Teams = Math.Max(2, ev.Teams),
                WithExits = true,
                Hint = Lang.Get("canevents:admin-arenas-openpads-hint")
            };
    }
}
