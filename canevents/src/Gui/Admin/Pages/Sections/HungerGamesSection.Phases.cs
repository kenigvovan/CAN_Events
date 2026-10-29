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
        private double BuildPhases(PageBuildContext ctx, ElementBounds column, PlaceListEntryMsg place,
                                   HgSetupMsg s, string[] codes, double y)
        {
            var gui = ctx.Gui;
            double width = column.fixedWidth - Card.Padding * 2;
            var phases = buf.EditPhases;
            int count = phases.Count;
            if (buf.PhaseSel >= count) buf.PhaseSel = -1;

            string hint = Lang.Get("canevents:admin-hg-phases-hint");
            double hintHeight = ButtonRow.MultiLineHeight(hint, width) + 4;

            // Block sizes are for this arena only; the largest phase always counts as 100%.
            float maxPct = count > 0 ? phases.Max(r => r.Radius) : 0f;
            float arenaR = s.ZoneSet ? s.ZoneRadius : 0f;
            float toBlocks = ShrinkingZone.ScaleFor(arenaR, maxPct);
            string warn = count > 0 && maxPct < 100f
                ? Lang.Get("canevents:admin-hg-phases-no-full", NumberParse.Format(Math.Round(maxPct))) : null;
            double warnHeight = warn != null ? ButtonRow.TextHeight(warn, width) + 4 : 0;

            double listHeight = Math.Min(Math.Max(count, 1), MaxVisibleRows) * ColumnTable.RowPitch + 10;
            double tableHeight = count > 0 ? ColumnTable.HeaderHeight + 4 + listHeight + 10 : LineHeight + 4;

            string addCaption = Lang.Get("canevents:admin-hg-phase-add");
            string applyCaption = buf.PhaseSel >= 0
                ? Lang.Get("canevents:admin-hg-phase-apply", buf.PhaseSel + 1)
                : Lang.Get("canevents:admin-hg-phase-apply-none");
            string saveCaption = Lang.Get("canevents:admin-hg-save");
            double buttonsHeight = ButtonRow.HeightFor(width, 0, addCaption, applyCaption, saveCaption);

            double contentHeight = hintHeight + 4 + tableHeight + warnHeight + InputRowHeight + buttonsHeight + LineHeight + 4;
            var inner = Section.Begin(ctx, column, y, "hg-phases", Lang.Get("canevents:admin-hg-phases-title"),
                contentHeight, out double nextY);
            if (inner == null) return nextY;

            double cy = inner.fixedY;
            ctx.Compo.AddStaticText(hint, AdminFonts.Hint, ElementBounds.Fixed(inner.fixedX, cy, width, hintHeight), "hg-phases-hint");
            cy += hintHeight + 4;

            if (count > 0)
            {
                var spec = new ColumnSpec(
                    new ColumnDef("#", 34),
                    new ColumnDef(Lang.Get("canevents:admin-hg-col-ends"), 80, EnumTextOrientation.Right),
                    new ColumnDef(Lang.Get("canevents:admin-hg-col-size"), 70, EnumTextOrientation.Right),
                    new ColumnDef(Lang.Get("canevents:admin-hg-col-dps"), 70, EnumTextOrientation.Right),
                    new ColumnDef(Lang.Get("canevents:admin-hg-col-blocks"), 0),
                    new ColumnDef(null, 150));

                var rows = new List<ColumnRow>();
                for (int i = 0; i < count; i++)
                {
                    var p = phases[i];
                    int index = i;
                    string blocks = arenaR > 0f && maxPct > 0f
                        ? Lang.Get("canevents:admin-hg-phase-blocks", NumberParse.Format(Math.Round(p.Radius * toBlocks, 1)))
                        : "";
                    var row = new ColumnRow((i + 1).ToString(), NumberParse.Format(p.End),
                        NumberParse.Format(p.Radius) + "%", NumberParse.Format(p.Dps), blocks, null)
                    {
                        Selected = i == buf.PhaseSel
                    };
                    row.WithColor(4, AdminColors.Muted);
                    row.WithButton(5, Lang.Get("canevents:admin-hg-edit"), () =>
                    {
                        buf.PhaseSel = index;
                        buf.PhaseEndInput = p.End;
                        buf.PhaseRadiusInput = p.Radius;
                        buf.PhaseDpsInput = p.Dps;
                        ctx.State.Drafts.Remove("hg-phase-end");
                        ctx.State.Drafts.Remove("hg-phase-size");
                        ctx.State.Drafts.Remove("hg-phase-dps");
                        gui.RequestRebuild();
                    }, Lang.Get("canevents:admin-hg-phase-edit-hint"));
                    row.WithButton(5, Lang.Get("canevents:admin-hg-delete"), () =>
                    {
                        phases.Remove(p);
                        if (buf.PhaseSel == index) buf.PhaseSel = -1;
                        else if (buf.PhaseSel > index) buf.PhaseSel--;
                        gui.RequestRebuild();
                    });
                    rows.Add(row);
                }
                ColumnTable.Add(ctx, ElementBounds.Fixed(inner.fixedX, cy, width, 0), spec, rows, listHeight,
                    new ScrollableListOptions { Key = "hg-phase-list" });
                cy += tableHeight;
            }
            else
            {
                ctx.Compo.AddStaticText(Lang.Get("canevents:admin-hg-phases-empty"),
                    CairoFont.WhiteSmallText().WithColor(AdminColors.Warning),
                    ElementBounds.Fixed(inner.fixedX, cy, width, LineHeight), "hg-phases-empty");
                cy += tableHeight;
            }

            if (warn != null)
            {
                ctx.Compo.AddStaticText(warn, CairoFont.WhiteSmallText().WithColor(AdminColors.Warning),
                    ElementBounds.Fixed(inner.fixedX, cy, width, warnHeight - 4), "hg-phases-warn");
                cy += warnHeight;
            }

            double x = AddLabel(ctx, Lang.Get("canevents:admin-hg-col-ends"), inner.fixedX, cy, "hg-phase-end-label");
            NumberField.Add(ctx, ElementBounds.Fixed(x, cy, 70, InputHeight), "hg-phase-end",
                () => buf.PhaseEndInput, v => buf.PhaseEndInput = v, 0, 100000, Lang.Get("canevents:admin-hg-phase-end-hint"));
            x += 70 + 12;
            x = AddLabel(ctx, Lang.Get("canevents:admin-hg-col-size"), x, cy, "hg-phase-size-label");
            NumberField.Add(ctx, ElementBounds.Fixed(x, cy, 60, InputHeight), "hg-phase-size",
                () => buf.PhaseRadiusInput, v => buf.PhaseRadiusInput = v, 0, 100, Lang.Get("canevents:admin-hg-phase-size-hint"));
            x += 60 + 12;
            x = AddLabel(ctx, Lang.Get("canevents:admin-hg-col-dps"), x, cy, "hg-phase-dps-label");
            NumberField.Add(ctx, ElementBounds.Fixed(x, cy, 60, InputHeight), "hg-phase-dps",
                () => buf.PhaseDpsInput, v => buf.PhaseDpsInput = v, 0, 10000, Lang.Get("canevents:admin-hg-phase-dps-hint"));
            cy += InputRowHeight;

            var buttons = new ButtonRow(ctx.Compo, ElementBounds.Fixed(inner.fixedX, cy, width, 0), width, 0, "hg-phase-btn");
            buttons.Add(addCaption, () =>
            {
                phases.Add(PhaseFromInputs());
                buf.PhaseSel = -1;
                gui.RequestRebuild();
            }, Lang.Get("canevents:admin-hg-phase-add-hint"));
            buttons.Add(applyCaption, () =>
            {
                if (buf.PhaseSel < 0 || buf.PhaseSel >= phases.Count)
                {
                    gui.SetStatus(false, Lang.Get("canevents:admin-hg-phase-apply-none-hint"));
                    return;
                }
                var p = phases[buf.PhaseSel];
                var n = PhaseFromInputs();
                p.End = n.End; p.Radius = n.Radius; p.Dps = n.Dps;
                gui.RequestRebuild();
            }, Lang.Get("canevents:admin-hg-phase-apply-hint"));
            buttons.Add(saveCaption, () => Save(gui, place, s, codes), Lang.Get("canevents:admin-hg-save-hint"));
            cy += buttonsHeight;

            Card.AddDirtyLine(ctx, inner.fixedX, cy, width, "hg-phases-dirty", buf.IsDirty, "canevents:admin-hg-");
            return nextY;
        }

        private HgPhaseRow PhaseFromInputs() => new HgPhaseRow
        {
            End = (float)Math.Max(0, buf.PhaseEndInput),
            Radius = (float)Math.Max(0, Math.Min(100, buf.PhaseRadiusInput)),
            Dps = (float)Math.Max(0, buf.PhaseDpsInput)
        };
    }
}
