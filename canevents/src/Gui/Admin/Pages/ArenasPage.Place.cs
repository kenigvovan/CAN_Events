using System;
using System.Collections.Generic;
using canevents.src.Gui.Admin.Dialogs;
using canevents.src.Gui.Admin.GuiElements;
using canevents.src.Gui.Admin.Pages.Sections;
using canevents.src.Gui.Admin.Widgets;
using canevents.src.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace canevents.src.Gui.Admin.Pages
{
    // Built inside a ScrollPane: y = 0 is the top of the scrolled content.
    public sealed partial class ArenasPage
    {
        private const int MaxSpawnRows = 12;

        // Off by default so applying bounds never wipes hand-placed spawns.
        private bool boundsGenerateSpawns;
        private int nudgeStep = 1;

        private double BuildPlaceTab(PageBuildContext ctx, PlaceListEntryMsg place)
        {
            var column = ctx.Column(0, ctx.Width);
            double y = 4;
            y = BuildPlaceHeader(ctx, column, place, y);
            // HG shows pedestals in its own section.
            if (!HungerGamesSection.AppliesTo(place)) y = BuildSpawns(ctx, column, place, y);
            y = BuildBounds(ctx, column, place, y);
            return y;
        }

        private double BuildPlaceHeader(PageBuildContext ctx, ElementBounds column, PlaceListEntryMsg place, double y)
        {
            var compo = ctx.Compo;
            double width = column.fixedWidth - Card.Padding * 2;

            string desc = string.IsNullOrWhiteSpace(place.Description) ? null : place.Description;
            double descHeight = desc != null ? ButtonRow.MultiLineHeight(desc, width) + 4 : 0;
            int summaryLines = place.BehaviorSummary?.Length ?? 0;

            string goCap = Lang.Get("canevents:admin-arenas-goto");
            string outlineCap = Lang.Get(Highlighter.Enabled ? "canevents:admin-arenas-outline-hide" : "canevents:admin-arenas-outline-show");
            string spawnCap = Lang.Get("canevents:admin-arenas-add-spawn");
            string toggleCap = Lang.Get(place.Enabled ? "canevents:admin-arenas-disable" : "canevents:admin-arenas-enable");
            string deleteCap = Lang.Get("canevents:admin-arenas-delete");
            string editCap = Lang.Get("canevents:admin-arenas-edit-info");
            double buttonsHeight = ButtonRow.HeightFor(width, 0, goCap, outlineCap, spawnCap, editCap, toggleCap, deleteCap);

            double content = descHeight + LineHeight * 2 + summaryLines * LineHeight + 8 + buttonsHeight;
            double height = Card.HeightAround(content);
            string title = (place.Name ?? "?") + " (#" + place.Id + ")";
            var inner = Card.Frame(compo, column, y, height, title);

            double cy = inner.fixedY;
            if (desc != null)
            {
                Wrapped(ctx, inner.fixedX, cy, width, desc, AdminFonts.Body);
                cy += descHeight;
            }

            if (place.HasBounds)
                Line(ctx, inner.fixedX, cy, width,
                    Lang.Get("canevents:admin-arenas-bounds-line", BoundsCorners(place), FormatBoundsSize(place)), AdminColors.Muted);
            else
                Line(ctx, inner.fixedX, cy, width, Lang.Get("canevents:admin-arenas-header-nobounds"), AdminColors.Danger);
            cy += LineHeight;

            Line(ctx, inner.fixedX, cy, width, SourceLine(place), AdminColors.Muted);
            cy += LineHeight;

            if (place.BehaviorSummary != null)
            {
                foreach (var line in place.BehaviorSummary)
                {
                    Line(ctx, inner.fixedX, cy, width, line ?? "", AdminColors.Success);
                    cy += LineHeight;
                }
            }
            cy += 8;

            long id = place.Id;
            string name = place.Name ?? "?";
            var row = new ButtonRow(compo, ElementBounds.Fixed(inner.fixedX, cy, width, 0), width, 0, "ar-place-act");
            row.Add(goCap, () => Act(Lang.Get("canevents:admin-arenas-log-goto", name), () => Net.SendTeleportToPlace(id)),
                Lang.Get("canevents:admin-arenas-goto-tip"));
            // Outline stays on after the panel closes.
            row.Add(outlineCap, () =>
            {
                Highlighter.SetEnabled(!Highlighter.Enabled);
                Rebuild();
            }, Lang.Get("canevents:admin-arenas-outline-tip"));
            row.Add(spawnCap, () => Act(Lang.Get("canevents:admin-arenas-log-add-spawn", name), () => Net.SendAddSpawnPoint(id)),
                Lang.Get("canevents:admin-arenas-add-spawn-tip"));
            row.Add(editCap, () => Gui.OpenSideDialog(new PlaceInfoPanel(place)),
                Lang.Get("canevents:admin-arenas-edit-info-tip"));
            bool enabled = place.Enabled;
            row.Add(toggleCap, () => Act(Lang.Get(enabled ? "canevents:admin-arenas-log-disable" : "canevents:admin-arenas-log-enable", name),
                    () => Net.SendTogglePlace(id)),
                Lang.Get(place.Enabled ? "canevents:admin-arenas-disable-tip" : "canevents:admin-arenas-enable-tip"));
            row.Add(deleteCap, () => OpenDelete(place), Lang.Get("canevents:admin-arenas-delete-tip"));

            return y + height + Card.Gap;
        }

        private void OpenDelete(PlaceListEntryMsg place)
        {
            long id = place.Id;
            string name = place.Name ?? "#" + id;
            string question = Lang.Get("canevents:admin-arenas-delete-question") + "\n\n" + name + " #" + id;
            Gui.OpenSideDialog(new ConfirmPanel(Lang.Get("canevents:admin-arenas-delete-title"), question, () =>
            {
                Act(Lang.Get("canevents:admin-arenas-log-delete", name, id), () => Net.SendRemovePlaceWithRestore(id));
                if (selectedPlaceId == id) selectedPlaceId = -1;
                bulkIds.Remove(id);
                Rebuild();
            }, "canevents:admin-arenas-delete-confirm"));
        }

        // Also used by WizardPage.
        internal double BuildSpawns(PageBuildContext ctx, ElementBounds column, PlaceListEntryMsg place, double y)
        {
            double width = column.fixedWidth - Card.Padding * 2;
            int count = place.SpawnX?.Length ?? 0;
            // Arrays are index-aligned; guard against a short one.
            if (place.SpawnY == null || place.SpawnZ == null) count = 0;
            else count = Math.Min(count, Math.Min(place.SpawnY.Length, place.SpawnZ.Length));

            string title = Lang.Get("canevents:admin-arenas-spawns-title", count);
            double listHeight = Math.Min(Math.Max(count, 1), MaxSpawnRows) * ColumnTable.RowPitch + 10;
            double tableHeight = ColumnTable.HeaderHeight + 4 + listHeight + 10;
            double content = count > 0 ? tableHeight : LineHeight;

            var inner = Section.Begin(ctx, column, y, "ar-spawns", title, content, out double nextY);
            if (inner == null) return nextY;

            if (count == 0)
            {
                Line(ctx, inner.fixedX, inner.fixedY, width, Lang.Get("canevents:admin-arenas-spawns-none"), AdminColors.Muted);
                return nextY;
            }

            var spec = new ColumnSpec(
                new ColumnDef(Lang.Get("canevents:admin-arenas-spawns-col-team"), 70),
                new ColumnDef(Lang.Get("canevents:admin-arenas-spawns-col-pos"), 0),
                new ColumnDef(null, 120),
                new ColumnDef(null, 290));

            var rows = new List<ColumnRow>();
            long id = place.Id;
            for (int i = 0; i < count; i++)
            {
                int index = i;
                int x = place.SpawnX[i], sy = place.SpawnY[i], z = place.SpawnZ[i];
                bool outside = !PlaceHighlighter.PositionInsideBounds(place, x, z);

                var row = new ColumnRow(Lang.Get("canevents:admin-arenas-spawns-team", i + 1),
                        "(" + x + ", " + sy + ", " + z + ")",
                        outside ? Lang.Get("canevents:admin-arenas-spawns-outside") : "", null)
                    .WithColor(0, outside ? AdminColors.Danger : AdminColors.Success)
                    .WithColor(1, AdminColors.Muted)
                    .WithColor(2, AdminColors.Danger);
                if (outside) row.WithCellTooltip(2, Lang.Get("canevents:admin-arenas-spawns-outside-tip"));

                row.WithButton(3, Lang.Get("canevents:admin-spawn-tp"), () =>
                    Act(Lang.Get("canevents:admin-log-spawn-tp", index + 1), () => Net.SendTeleportToSpawn(id, index)),
                    Lang.Get("canevents:admin-spawn-tp-tip"));

                // Move in place: remove + add would reassign the team's corner.
                row.WithButton(3, Lang.Get("canevents:admin-arenas-spawns-move"), () =>
                    Act(Lang.Get("canevents:admin-arenas-log-move-spawn", index + 1), () => Net.SendMoveSpawnPoint(id, index)),
                    Lang.Get("canevents:admin-arenas-spawns-move-tip"));

                ArmedTextButton.AddToRow(ctx, row, 3, "ar-spawn-rm-" + id + "-" + i,
                    Lang.Get("canevents:admin-arenas-remove"), Lang.Get("canevents:admin-arenas-remove-armed"),
                    Lang.Get("canevents:admin-arenas-spawns-remove-tip"),
                    () => Act(Lang.Get("canevents:admin-arenas-log-remove-spawn", index + 1), () => Net.SendRemoveSpawnPoint(id, index)));
                rows.Add(row);
            }

            var anchor = ElementBounds.Fixed(inner.fixedX, inner.fixedY, width, 0);
            ColumnTable.Add(ctx, anchor, spec, rows, listHeight, new ScrollableListOptions { Key = "ar-spawn-list" });
            return nextY;
        }

        // Without bounds every /canep command silently ignores the place. Also used by WizardPage.
        internal double BuildBounds(PageBuildContext ctx, ElementBounds column, PlaceListEntryMsg place, double y)
        {
            var compo = ctx.Compo;
            var hl = Highlighter;
            double width = column.fixedWidth - Card.Padding * 2;

            string title = place.HasBounds
                ? Lang.Get("canevents:admin-arenas-bounds-title", FormatBoundsSize(place), BoundsCorners(place))
                : Lang.Get("canevents:admin-arenas-bounds-title-none");

            string intro = Lang.Get("canevents:admin-arenas-bounds-intro");
            double introHeight = ButtonRow.MultiLineHeight(intro, width) + 4;

            bool haveTarget = hl.TryGetMarkTarget(out var target, out bool fromLook);
            string targetLine = haveTarget
                ? Lang.Get(fromLook ? "canevents:admin-arenas-mark-target-look" : "canevents:admin-arenas-mark-target-feet",
                    target.X, target.Y, target.Z)
                : Lang.Get("canevents:admin-arenas-mark-target-none");
            string howTo = Lang.Get("canevents:admin-arenas-mark-howto");

            string mark1Cap = Lang.Get("canevents:admin-arenas-mark1");
            string mark2Cap = Lang.Get("canevents:admin-arenas-mark2");
            string clearMarksCap = Lang.Get("canevents:admin-arenas-mark-clear");
            bool anyMark = hl.HasMark1 || hl.HasMark2;
            bool bothMarks = hl.HasMark1 && hl.HasMark2;
            string staysNote = Lang.Get("canevents:admin-arenas-mark-stays");
            double staysHeight = anyMark ? ButtonRow.TextHeight(staysNote, width) : 0;

            string applyCap = Lang.Get(place.HasBounds ? "canevents:admin-arenas-bounds-replace" : "canevents:admin-arenas-bounds-set");
            string clearCap = Lang.Get("canevents:admin-arenas-bounds-clear");
            string clearArmed = Lang.Get("canevents:admin-arenas-bounds-clear-armed");
            double applyHeight = place.HasBounds
                ? ButtonRow.HeightFor(width, 0, applyCap, ArmedTextButton.WiderCaption(clearCap, clearArmed))
                : ButtonRow.ButtonExtent;

            bool canNudge = bothMarks || place.HasBounds;
            string growCap = Lang.Get("canevents:admin-arenas-nudge-grow", nudgeStep);
            string shrinkCap = Lang.Get("canevents:admin-arenas-nudge-shrink", nudgeStep);
            string stepCap = Lang.Get("canevents:admin-arenas-nudge-step");
            var faceCaps = Array.ConvertAll(NudgeFaces, f => Lang.Get("canevents:admin-arenas-face-" + f.Code));
            var stepCaps = Array.ConvertAll(NudgeSteps, s => s.ToString());
            var labelFont = CairoFont.WhiteSmallText();
            double nudgeLabelWidth = Math.Max(labelFont.GetTextExtents(stepCap).Width,
                Math.Max(labelFont.GetTextExtents(growCap).Width, labelFont.GetTextExtents(shrinkCap).Width)) + 10;
            double stepRow = ButtonRow.HeightFor(width, nudgeLabelWidth, stepCaps) + 4;
            double nudgeRow = ButtonRow.HeightFor(width, nudgeLabelWidth, faceCaps) + 4;
            double nudgeHeight = stepRow + nudgeRow * 2;

            double markRow = ButtonRow.ButtonExtent + 4;
            double content = introHeight + LineHeight * 2 + 4 + ToggleRow + markRow * 2 + staysHeight
                             + (bothMarks ? LineHeight : 0) + nudgeHeight + 4 + ToggleRow + applyHeight;

            double cardHeight = Card.HeightAround(content);
            var inner = Card.Frame(compo, column, y, cardHeight, title);
            double nextY = y + cardHeight + Card.Gap;

            double cy = inner.fixedY;
            Wrapped(ctx, inner.fixedX, cy, width, intro, AdminFonts.Hint);
            cy += introHeight;

            Line(ctx, inner.fixedX, cy, width, targetLine, haveTarget ? AdminColors.Label : AdminColors.Danger);
            cy += LineHeight;
            Line(ctx, inner.fixedX, cy, width, howTo, AdminColors.Muted);
            cy += LineHeight + 4;

            AddToggle(ctx, inner.fixedX, cy, width, "ar-mark-feet", hl.MarkAtFeet, on =>
            {
                hl.MarkAtFeet = on;
                Rebuild(); // the "next mark" line depends on it
            }, Lang.Get("canevents:admin-arenas-mark-at-feet"), Lang.Get("canevents:admin-arenas-mark-at-feet-tip"));
            cy += ToggleRow;

            AddMarkRow(ctx, inner.fixedX, cy, width, 1, mark1Cap, haveTarget, hl.HasMark1,
                hl.Mark1X, hl.Mark1Y, hl.Mark1Z, Lang.Get("canevents:admin-arenas-mark1-tip"), null);
            cy += markRow;
            AddMarkRow(ctx, inner.fixedX, cy, width, 2, mark2Cap, haveTarget, hl.HasMark2,
                hl.Mark2X, hl.Mark2Y, hl.Mark2Z, Lang.Get("canevents:admin-arenas-mark2-tip"),
                anyMark ? clearMarksCap : null);
            cy += markRow;

            if (anyMark)
            {
                compo.AddStaticText(staysNote, AdminFonts.Hint, ElementBounds.Fixed(inner.fixedX, cy, width, staysHeight), K("ar-mark-stays"));
                cy += staysHeight;
            }
            if (bothMarks)
            {
                int sx = Math.Abs(hl.Mark2X - hl.Mark1X) + 1;
                int sz = Math.Abs(hl.Mark2Z - hl.Mark1Z) + 1;
                Line(ctx, inner.fixedX, cy, width, Lang.Get("canevents:admin-arenas-mark-area", sx, sz), AdminColors.Success);
                cy += LineHeight;
            }
            compo.AddStaticText(stepCap, AdminFonts.Body,
                ElementBounds.Fixed(inner.fixedX, cy + 6, nudgeLabelWidth, LineHeight), K("ar-nudge-step-label"));
            var stepButtons = new ButtonRow(compo, ElementBounds.Fixed(inner.fixedX, cy, width, 0), width, nudgeLabelWidth, "ar-nudge-step");
            for (int i = 0; i < NudgeSteps.Length; i++)
            {
                int step = NudgeSteps[i];
                bool active = step == nudgeStep;
                // Same look as the leaderboard filters: the chosen one is flat, in the active text colour.
                var stepFont = CairoFont.ButtonText();
                if (active) stepFont.Color = (double[])GuiStyle.ActiveButtonTextColor.Clone();
                compo.AddButton(stepCaps[i], new ActionConsumable(() => { nudgeStep = step; Rebuild(); return true; }),
                    stepButtons.Next(ButtonRow.ButtonWidth(stepCaps[i])), stepFont,
                    active ? EnumButtonStyle.Small : EnumButtonStyle.Normal, K("ar-nudge-step"));
            }
            cy += stepRow;

            AddNudgeRow(ctx, inner.fixedX, cy, width, nudgeLabelWidth, growCap, faceCaps, place, 1, canNudge);
            cy += nudgeRow;
            AddNudgeRow(ctx, inner.fixedX, cy, width, nudgeLabelWidth, shrinkCap, faceCaps, place, -1, canNudge);
            cy += nudgeRow + 4;

            AddToggle(ctx, inner.fixedX, cy, width, "ar-bnd-gen", boundsGenerateSpawns, on => boundsGenerateSpawns = on,
                Lang.Get("canevents:admin-arenas-bounds-gen-spawns"), Lang.Get("canevents:admin-arenas-bounds-gen-spawns-tip"));
            cy += ToggleRow;

            long id = place.Id;
            string name = place.Name ?? "?";
            var applyRow = new ButtonRow(compo, ElementBounds.Fixed(inner.fixedX, cy, width, 0), width, 0, "ar-bnd-apply");
            Button(ctx, applyRow, applyCap, () =>
            {
                if (!hl.HasMark1 || !hl.HasMark2) return;
                int x1 = hl.Mark1X, y1 = hl.Mark1Y, z1 = hl.Mark1Z, x2 = hl.Mark2X, y2 = hl.Mark2Y, z2 = hl.Mark2Z;
                bool gen = boundsGenerateSpawns;
                Act(Lang.Get("canevents:admin-arenas-log-set-bounds", name),
                    () => Net.SendSetPlaceBounds(id, x1, y1, z1, x2, y2, z2, gen));
                // Keep the marks, or the outline blinks out until the place list arrives.
                hl.HighlightedPlaceId = -2;
            }, bothMarks ? Lang.Get("canevents:admin-arenas-bounds-apply-tip") : Lang.Get("canevents:admin-arenas-bounds-need-marks"),
                enabled: bothMarks);

            if (place.HasBounds)
            {
                ArmedTextButton.Add(ctx, applyRow.Next(ArmedTextButton.Width(clearCap, clearArmed)), "ar-bnd-clear-" + id, clearCap, () =>
                {
                    Act(Lang.Get("canevents:admin-arenas-log-clear-bounds", name), () => Net.SendClearPlaceBounds(id));
                    hl.HighlightedPlaceId = -2;
                }, clearArmed, Lang.Get("canevents:admin-arenas-bounds-clear-tip"));
            }

            return nextY;
        }

        private static readonly BlockFacing[] NudgeFaces =
            { BlockFacing.UP, BlockFacing.DOWN, BlockFacing.NORTH, BlockFacing.SOUTH, BlockFacing.WEST, BlockFacing.EAST };

        private static readonly int[] NudgeSteps = { 1, 5, 10, 25 };

        // nudgeStep blocks per click on the marked box; saved bounds are copied into the marks on the first click.
        private void AddNudgeRow(PageBuildContext ctx, double x, double y, double width, double labelWidth,
                                 string label, string[] faceCaps, PlaceListEntryMsg place, int delta, bool enabled)
        {
            var hl = Highlighter;
            ctx.Compo.AddStaticText(label, AdminFonts.Body, ElementBounds.Fixed(x, y + 6, labelWidth, LineHeight), K("ar-nudge-label"));

            var row = new ButtonRow(ctx.Compo, ElementBounds.Fixed(x, y, width, 0), width, labelWidth, delta > 0 ? "ar-nudge-grow" : "ar-nudge-shrink");
            for (int i = 0; i < NudgeFaces.Length; i++)
            {
                var face = NudgeFaces[i];
                Button(ctx, row, faceCaps[i], () =>
                {
                    if (!hl.HasMark1 || !hl.HasMark2)
                    {
                        if (!place.HasBounds) return;
                        hl.MarksFromBounds(place);
                    }
                    if (!hl.NudgeMarks(face, delta * nudgeStep)) Gui.SetStatus(false, Lang.Get("canevents:admin-arenas-nudge-too-small"));
                    Rebuild();
                }, Lang.Get(enabled ? "canevents:admin-arenas-face-" + face.Code + "-tip" : "canevents:admin-arenas-nudge-need-box"),
                    enabled);
            }
        }

        private const double ToggleSize = 22;
        private const double ToggleRow = ToggleSize + 8;

        // Switch with a one-line label; the hover tip covers both.
        private void AddToggle(PageBuildContext ctx, double x, double y, double width, string key, bool on,
                               Action<bool> onToggle, string label, string tip)
        {
            string k = K(key);
            ctx.Compo.AddSwitch(onToggle, ElementBounds.Fixed(x, y, ToggleSize, ToggleSize), k, ToggleSize, 3);
            ctx.Compo.GetSwitch(k).On = on;

            double lw = width - ToggleSize - 8;
            var font = AdminFonts.Body;
            var labelBounds = ElementBounds.Fixed(x + ToggleSize + 8, y + 2, lw, ToggleSize);
            ctx.Compo.AddStaticText(Card.Fit(label, font, lw), font, labelBounds, k + "-label");
            Tooltip.Add(ctx.Compo, tip, ElementBounds.Fixed(x, y, width, ToggleSize), k + "-tip");
        }

        private void AddMarkRow(PageBuildContext ctx, double x, double y, double width, int which, string caption,
                                bool haveTarget, bool has, int mx, int my, int mz, string tooltip, string clearCaption)
        {
            var hl = Highlighter;
            var row = new ButtonRow(ctx.Compo, ElementBounds.Fixed(x, y, width, 0), width, 0, "ar-mark" + which);
            var b = Button(ctx, row, caption, () =>
            {
                if (!hl.TakeMark(which)) Gui.SetStatus(false, Lang.Get("canevents:admin-arenas-mark-target-none"));
                Rebuild();
            }, tooltip, enabled: haveTarget);

            string value = has ? "(" + mx + ", " + my + ", " + mz + ")" : Lang.Get("canevents:admin-arenas-mark-notset");
            var font = CairoFont.WhiteSmallText().WithColor(has ? AdminColors.Success : AdminColors.Muted);
            double vx = b.fixedX + b.fixedWidth + 10;
            double vw = Math.Min(200, x + width - vx);
            if (vw > 20)
            {
                ctx.Compo.AddStaticText(Card.Fit(value, font, vw), font, ElementBounds.Fixed(vx, y + 6, vw, LineHeight), K("ar-mark-val"));
            }

            if (clearCaption != null)
            {
                double cw = ButtonRow.ButtonWidth(clearCaption);
                double cx = Math.Max(vx + vw + 10, x + width - cw);
                if (cx + cw <= x + width + 0.5)
                {
                    var cb = ElementBounds.Fixed(cx, y, cw, ButtonRow.ButtonHeight);
                    ctx.Compo.AddButton(clearCaption, new ActionConsumable(() =>
                    {
                        hl.ClearMarks();
                        Rebuild();
                        return true;
                    }), cb, EnumButtonStyle.Normal, K("ar-mark-clear"));
                }
            }
        }
    }
}
