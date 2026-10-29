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
        private double BuildLoot(PageBuildContext ctx, ElementBounds column, PlaceListEntryMsg place,
                                 HgSetupMsg s, string[] tiers, double y)
        {
            var gui = ctx.Gui;
            var net = gui.Net;
            double width = column.fixedWidth - Card.Padding * 2;

            if (tiers != null && (buf.LootTierIdx < 0 || buf.LootTierIdx >= tiers.Length)) buf.LootTierIdx = 0;
            int tierIdx = tiers != null ? buf.LootTierIdx : -1;

            // Free-text tier when the server has no event config to list tiers from.
            string tierLabel = Lang.Get("canevents:admin-hg-loot-tier");
            double tierLabelWidth = CairoFont.WhiteSmallText().GetTextExtents(tierLabel).Width + 12;
            string[] tierCaptions = null;
            double pickerHeight;
            if (tiers != null)
            {
                tierCaptions = new string[tiers.Length];
                for (int i = 0; i < tiers.Length; i++)
                    tierCaptions[i] = i == tierIdx ? "> " + tiers[i] + " <" : tiers[i];
                pickerHeight = ButtonRow.HeightFor(width, tierLabelWidth, tierCaptions);
            }
            else
            {
                pickerHeight = InputRowHeight;
            }

            string markerLine = tiers != null
                ? Lang.Get("canevents:admin-hg-marker-tier", tiers[tierIdx])
                : Lang.Get("canevents:admin-hg-marker-neutral");
            string summary = tiers != null && s?.TierSummaries != null && tierIdx < s.TierSummaries.Length
                ? s.TierSummaries[tierIdx] : null;
            double summaryHeight = !string.IsNullOrEmpty(summary) ? ButtonRow.MultiLineHeight(summary, width) + 4 : 0;

            string addCaption = Lang.Get("canevents:admin-hg-loot-add");
            string clearCaption = Lang.Get("canevents:admin-hg-loot-clear");
            string clearArmed = Lang.Get("canevents:admin-hg-loot-clear-armed");
            string refreshCaption = Lang.Get("canevents:admin-hg-refresh");
            string clearMeasure = ArmedTextButton.WiderCaption(clearCaption, clearArmed);
            double buttonsHeight = ButtonRow.HeightFor(width, 0, addCaption, clearMeasure, refreshCaption);

            int count = s?.SpotX == null || s.SpotY == null || s.SpotZ == null ? 0
                : Math.Min(s.SpotX.Length, Math.Min(s.SpotY.Length, s.SpotZ.Length));
            double listHeight = Math.Min(count, MaxVisibleRows) * ColumnTable.RowPitch + 10;
            double tableHeight = count > 0 ? ColumnTable.HeaderHeight + 4 + listHeight + 10 : 0;

            double contentHeight = pickerHeight + 4 + LineHeight + summaryHeight + 4 + buttonsHeight
                                   + LineHeight + 4 + tableHeight;
            var inner = Section.Begin(ctx, column, y, "hg-loot", Lang.Get("canevents:admin-hg-loot-title"),
                contentHeight, out double nextY);
            if (inner == null) return nextY;

            markerGui = gui;
            // Main composer, not ctx.Compo: inside a ScrollPane ctx.Compo is the pane's.
            markerComposerBounds = gui.MainDialogBounds;
            markerTierIdx = tierIdx;

            double cy = inner.fixedY;
            ctx.Compo.AddStaticText(tierLabel, CairoFont.WhiteSmallText().WithColor(AdminColors.Label),
                ElementBounds.Fixed(inner.fixedX, cy + 6, tierLabelWidth, LineHeight), "hg-loot-tier-label");
            if (tiers != null)
            {
                var picker = new ButtonRow(ctx.Compo, ElementBounds.Fixed(inner.fixedX, cy, width, 0), width, tierLabelWidth, "hg-loot-tier");
                for (int i = 0; i < tiers.Length; i++)
                {
                    int pick = i;
                    picker.Add(tierCaptions[i], () =>
                    {
                        buf.LootTierIdx = pick;
                        gui.RequestRebuild();
                    }, Lang.Get("canevents:admin-hg-loot-tier-hint"));
                }
            }
            else
            {
                NumberField.AddText(ctx, ElementBounds.Fixed(inner.fixedX + tierLabelWidth, cy + 1, 160, InputHeight), "hg-loot-tier-text",
                    () => buf.LootTier, v => buf.LootTier = v, Lang.Get("canevents:admin-hg-loot-tier-free-hint"));
            }
            cy += pickerHeight + 4;

            var markerFont = CairoFont.WhiteSmallText().WithColor(TierColor(tierIdx));
            ctx.Compo.AddStaticText(Card.Fit(markerLine, markerFont, width), markerFont,
                ElementBounds.Fixed(inner.fixedX, cy, width, LineHeight), "hg-loot-marker");
            cy += LineHeight;
            if (summaryHeight > 0)
            {
                ctx.Compo.AddStaticText(summary, AdminFonts.Hint,
                    ElementBounds.Fixed(inner.fixedX, cy, width, summaryHeight), "hg-loot-summary");
                cy += summaryHeight;
            }
            cy += 4;

            var buttons = new ButtonRow(ctx.Compo, ElementBounds.Fixed(inner.fixedX, cy, width, 0), width, 0, "hg-loot-btn");
            buttons.Add(addCaption, () =>
            {
                string tier = tiers != null
                    ? tiers[Math.Min(buf.LootTierIdx, tiers.Length - 1)]
                    : (string.IsNullOrWhiteSpace(buf.LootTier) ? null : buf.LootTier.Trim());
                gui.Act(Lang.Get("canevents:admin-hg-log-loot-add", tier ?? "-", place.Name ?? "?"),
                    () => net.SendAddHgLoot(place.Id, tier));
            }, Lang.Get("canevents:admin-hg-loot-add-hint"));
            ArmedTextButton.Add(ctx, buttons.Next(ArmedTextButton.Width(clearCaption, clearArmed)), "hg-loot-clear",
                clearCaption,
                () => gui.Act(Lang.Get("canevents:admin-hg-log-loot-clear", place.Name ?? "?"), () => net.SendClearHgLoot(place.Id)),
                clearArmed, Lang.Get("canevents:admin-hg-loot-clear-hint"));
            buttons.Add(refreshCaption, () =>
            {
                net.SendRequestHgSetup(place.Id, buf.CfgEventCode);
                net.RequestPlaceList();
            }, Lang.Get("canevents:admin-hg-refresh-hint"));
            cy += buttonsHeight;

            string countText = s == null
                ? Lang.Get("canevents:admin-hg-loading")
                : count == 0 ? Lang.Get("canevents:admin-hg-loot-none") : Lang.Get("canevents:admin-hg-loot-count", count);
            var countFont = CairoFont.WhiteSmallText().WithColor(s == null ? AdminColors.Muted : count == 0 ? AdminColors.Warning : AdminColors.Success);
            ctx.Compo.AddStaticText(Card.Fit(countText, countFont, width), countFont,
                ElementBounds.Fixed(inner.fixedX, cy, width, LineHeight), "hg-loot-count");
            cy += LineHeight + 4;

            if (count > 0) AddLootTable(ctx, place, s, tiers, ElementBounds.Fixed(inner.fixedX, cy, width, 0), count, listHeight);
            return nextY;
        }

        private void AddLootTable(PageBuildContext ctx, PlaceListEntryMsg place, HgSetupMsg s, string[] tiers,
                                  ElementBounds anchor, int count, double listHeight)
        {
            var gui = ctx.Gui;
            var net = gui.Net;

            var spec = new ColumnSpec(
                new ColumnDef("#", 34),
                new ColumnDef(Lang.Get("canevents:admin-hg-col-position"), 0),
                new ColumnDef(Lang.Get("canevents:admin-hg-col-tier"), 110),
                new ColumnDef(null, 210));

            var rows = new List<ColumnRow>();
            for (int i = 0; i < count; i++)
            {
                int spot = i;
                int x = s.SpotX[i], py = s.SpotY[i], z = s.SpotZ[i];
                string tier = s.SpotTier != null && i < s.SpotTier.Length ? s.SpotTier[i] : null;
                int tierIdx = PlaceHighlighter.TierIndexOf(tiers, tier);
                bool inside = PlaceHighlighter.PositionInsideBounds(place, x, z);

                var row = new ColumnRow((i + 1).ToString(), "(" + x + ", " + py + ", " + z + ")", tier ?? "-", null);
                row.WithColor(1, inside ? AdminColors.Value : AdminColors.Danger);
                if (!inside) row.WithCellTooltip(1, Lang.Get("canevents:admin-hg-loot-outside-hint"));
                row.WithColor(2, TierColor(tierIdx));

                row.WithButton(3, Lang.Get("canevents:admin-hg-loot-tp"), () =>
                    gui.Act(Lang.Get("canevents:admin-hg-log-loot-tp", spot + 1, place.Name ?? "?"),
                        () => net.SendTeleportToHgLoot(place.Id, spot)),
                    Lang.Get("canevents:admin-hg-loot-tp-hint"));

                if (tiers != null)
                {
                    row.WithButton(3, Lang.Get("canevents:admin-hg-loot-retier"), () =>
                        gui.OpenSideDialog(new Dialogs.ChoicePanel(RetierRow(gui, place, spot, tier, tiers))),
                        Lang.Get("canevents:admin-hg-loot-retier-hint"));
                }

                ArmedTextButton.AddToRow(ctx, row, 3, "hg-loot-rm-" + place.Id + "-" + i,
                    Lang.Get("canevents:admin-hg-remove"), Lang.Get("canevents:admin-hg-remove-armed"),
                    Lang.Get("canevents:admin-hg-loot-remove-hint"),
                    () => gui.Act(Lang.Get("canevents:admin-hg-log-loot-remove", spot + 1, place.Name ?? "?"),
                        () => net.SendRemoveHgLoot(place.Id, spot)));
                rows.Add(row);
            }

            ColumnTable.Add(ctx, anchor, spec, rows, listHeight, new ScrollableListOptions { Key = "hg-loot-list" });
        }

        private static SettingRow RetierRow(AdminPanelGui gui, PlaceListEntryMsg place, int spot, string current, string[] tiers)
        {
            var options = new SettingOption[tiers.Length];
            for (int i = 0; i < tiers.Length; i++) options[i] = new SettingOption(tiers[i], tiers[i]);

            return SettingRow.Choice("hg-retier", Lang.Get("canevents:admin-hg-retier-title", spot + 1),
                () => current,
                picked =>
                {
                    if (string.Equals(picked, current, StringComparison.OrdinalIgnoreCase)) return;
                    gui.Act(Lang.Get("canevents:admin-hg-log-loot-retier", spot + 1, picked),
                        () => gui.Net.SendSetHgLootTier(place.Id, spot, picked));
                },
                options);
        }
    }
}
