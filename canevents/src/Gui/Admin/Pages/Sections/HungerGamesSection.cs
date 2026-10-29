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
    // HG setup card for one arena, drawn under the selected place by the Arenas page and the wizard.
    // Host must call Watch and forward WantsPlacementMarker, OnShown and OnHidden.
    public sealed partial class HungerGamesSection
    {
        private const double LineHeight = 20;
        private const double InputHeight = 26;
        private const double InputRowHeight = InputHeight + 8;
        private const double MaxVisibleRows = 5;
        // Pedestals are walked one by one, so show more of them than the other lists.
        private const double MaxSpawnRows = 12;
        private const double ChecklistGap = 14;

        private readonly HgSettingsBuffer buf = new HgSettingsBuffer();

        // Tied to the composer bounds (new each rebuild), so a rebuild without this section drops the marker.
        private AdminPanelGui markerGui;
        private ElementBounds markerComposerBounds;
        private int markerTierIdx = -1;

        public HgSettingsBuffer Buffer => buf;

        public static bool AppliesTo(PlaceListEntryMsg place) => PlaceHighlighter.HasType(place, PlaceTags.HungerGames);

        public void Watch(PageWatchList w, ClientNetworkHandler net)
        {
            w.Add(net.HgSetup);
            w.Add(net.PlaceList);
        }

        // Unsaved table edits survive this.
        public void OnShown() => buf.Invalidate();

        public void OnHidden()
        {
            markerComposerBounds = null;
            markerGui = null;
        }

        // Polled every frame. tierIdx -1 = neutral colour.
        public bool WantsPlacementMarker(out int tierIdx)
        {
            tierIdx = markerTierIdx;
            return markerGui != null && markerComposerBounds != null
                   && ReferenceEquals(markerGui.MainDialogBounds, markerComposerBounds);
        }

        // column null = full content width.
        public double Build(PageBuildContext ctx, PlaceListEntryMsg place, double y, ElementBounds column = null)
        {
            markerComposerBounds = null;
            if (place == null) return y;
            column = column ?? ctx.Column(0, ctx.Width);

            // Cheap when nothing changed.
            ctx.Gui.Highlighter.Update(place);
            RequestSetupIfStale(ctx.Gui.Net, ctx.Gui.Highlighter, place);

            var setup = ctx.Gui.Highlighter.HgSetupFor(place);
            string[] tiers = setup?.Tiers != null && setup.Tiers.Length > 0 ? setup.Tiers : null;

            y = BuildOverview(ctx, column, place, setup, y);
            y = BuildHelp(ctx, column, y);
            y = BuildLayout(ctx, column, place, setup, y);
            y = BuildZone(ctx, column, place, setup, y);
            y = BuildSpawns(ctx, column, place, setup, y);
            y = BuildLoot(ctx, column, place, setup, tiers, y);
            if (setup != null && !string.IsNullOrEmpty(setup.EventCode))
                y = BuildConfig(ctx, column, place, setup, y);
            return y;
        }

        // Wizard spawns step: layout generator and pedestals only, same keys as Build.
        public double BuildPedestals(PageBuildContext ctx, PlaceListEntryMsg place, double y, ElementBounds column = null)
        {
            markerComposerBounds = null;
            if (place == null) return y;
            column = column ?? ctx.Column(0, ctx.Width);

            ctx.Gui.Highlighter.Update(place);
            RequestSetupIfStale(ctx.Gui.Net, ctx.Gui.Highlighter, place);

            var setup = ctx.Gui.Highlighter.HgSetupFor(place);
            y = BuildLayout(ctx, column, place, setup, y);
            y = BuildSpawns(ctx, column, place, setup, y);
            return y;
        }

        // Re-ask on place changes too: the server only pushes a fresh setup after HG-specific edits.
        private void RequestSetupIfStale(ClientNetworkHandler net, PlaceHighlighter highlighter, PlaceListEntryMsg place)
        {
            int fingerprint = PlaceHighlighter.PlaceFingerprint(place);
            if (buf.SetupReqPlace != place.Id)
            {
                bool otherPlace = buf.ZoneRadiusFor != place.Id && buf.GenSpawnsFor != place.Id;
                buf.SetupReqPlace = place.Id;
                if (otherPlace)
                {
                    buf.LootTierIdx = 0;
                    buf.CfgEventCode = null; // another place may run a different set of HG variants
                }
                buf.CfgReqAfterMsg = null;
                net.SendRequestHgSetup(place.Id, otherPlace ? null : buf.CfgEventCode);
                if (highlighter.HgPreviewOn) highlighter.SetHgPreview(place.Id, true);
            }
            else if (fingerprint != buf.SetupReqFingerprint)
            {
                net.SendRequestHgSetup(place.Id, buf.CfgEventCode);
            }
            buf.SetupReqFingerprint = fingerprint;
        }

        private double BuildOverview(PageBuildContext ctx, ElementBounds column, PlaceListEntryMsg place,
                                     HgSetupMsg setup, double y)
        {
            var gui = ctx.Gui;
            double width = column.fixedWidth - Card.Padding * 2;

            string overview = setup?.Overview;
            double overviewHeight = string.IsNullOrEmpty(overview) ? 0 : ButtonRow.MultiLineHeight(overview, width) + 8;

            var checks = setup != null ? Checklist(setup) : null;
            double checksHeight = checks != null ? LayoutChecks(checks, width, null) + 4 : LineHeight + 4;

            var warnings = new List<string>();
            if (setup?.Warnings != null)
                foreach (var w in setup.Warnings)
                    if (!string.IsNullOrEmpty(w)) warnings.Add("! " + w);
            if (place.HasBounds && !gui.Highlighter.PlayerInsideBounds(place))
                warnings.Add(Lang.Get("canevents:admin-hg-outside-bounds"));
            double warningsHeight = 0;
            foreach (var w in warnings) warningsHeight += ButtonRow.TextHeight(w, width) + 2;

            double contentHeight = overviewHeight + checksHeight + warningsHeight;
            string title = Lang.Get("canevents:admin-hg-title", place.Name ?? "?", place.Id);
            var inner = Section.Begin(ctx, column, y, "hg-overview", title, contentHeight, out double nextY);
            if (inner == null) return nextY;

            double cy = inner.fixedY;
            if (overviewHeight > 0)
            {
                ctx.Compo.AddStaticText(overview, AdminFonts.Body,
                    ElementBounds.Fixed(inner.fixedX, cy, width, overviewHeight - 4), "hg-overview-text");
                cy += overviewHeight;
            }

            if (checks != null)
            {
                LayoutChecks(checks, width, (i, x, rowY, w) =>
                {
                    var (ok, label, tip) = checks[i];
                    var font = CairoFont.WhiteSmallText().WithColor(ok ? AdminColors.Success : AdminColors.Warning);
                    var b = ElementBounds.Fixed(inner.fixedX + x, cy + rowY, w, LineHeight);
                    ctx.Compo.AddStaticText(CheckCaption(ok, label), font, b, "hg-check-" + i);
                    if (tip != null) Tooltip.Add(ctx.Compo, tip, b, "hg-check-tip-" + i);
                });
                cy += checksHeight;
            }
            else
            {
                ctx.Compo.AddStaticText(Lang.Get("canevents:admin-hg-loading"), AdminFonts.Hint,
                    ElementBounds.Fixed(inner.fixedX, cy, width, LineHeight), "hg-overview-loading");
                cy += checksHeight;
            }

            for (int i = 0; i < warnings.Count; i++)
            {
                double h = ButtonRow.TextHeight(warnings[i], width);
                ctx.Compo.AddStaticText(warnings[i], CairoFont.WhiteSmallText().WithColor(AdminColors.Warning),
                    ElementBounds.Fixed(inner.fixedX, cy, width, h), "hg-warning-" + i);
                cy += h + 2;
            }
            return nextY;
        }

        private static List<(bool ok, string label, string tip)> Checklist(HgSetupMsg s)
        {
            int lootN = s.SpotX?.Length ?? 0;
            return new List<(bool, string, string)>
            {
                (s.HasBounds, Lang.Get("canevents:admin-hg-check-bounds"), Lang.Get("canevents:admin-hg-check-bounds-hint")),
                (s.RequiredSpawns > 0 && s.SpawnCount >= s.RequiredSpawns,
                    Lang.Get("canevents:admin-hg-check-spawns", s.SpawnCount, s.RequiredSpawns),
                    Lang.Get("canevents:admin-hg-check-spawns-hint")),
                (s.ZoneSet, s.ZoneSet
                    ? Lang.Get("canevents:admin-hg-check-zone-r", NumberParse.Format(Math.Round(s.ZoneRadius, 1)))
                    : Lang.Get("canevents:admin-hg-check-zone"), Lang.Get("canevents:admin-hg-check-zone-hint")),
                (lootN > 0, Lang.Get("canevents:admin-hg-check-loot", lootN), Lang.Get("canevents:admin-hg-check-loot-hint")),
                (s.UsesCornucopiaTier, Lang.Get("canevents:admin-hg-check-cornucopia"), Lang.Get("canevents:admin-hg-check-cornucopia-hint"))
            };
        }

        private static string CheckCaption(bool ok, string label) => (ok ? "[+] " : "[-] ") + label;

        // Returns the height; with place != null also positions each item.
        private static double LayoutChecks(List<(bool ok, string label, string tip)> checks, double width,
                                           Action<int, double, double, double> place)
        {
            var font = CairoFont.WhiteSmallText();
            double x = 0, rowY = 0;
            for (int i = 0; i < checks.Count; i++)
            {
                double w = font.GetTextExtents(CheckCaption(checks[i].ok, checks[i].label)).Width + 6;
                if (x > 0 && x + w > width)
                {
                    x = 0;
                    rowY += LineHeight;
                }
                place?.Invoke(i, x, rowY, w);
                x += w + ChecklistGap;
            }
            return rowY + LineHeight;
        }

        private static double BuildHelp(PageBuildContext ctx, ElementBounds column, double y)
        {
            string text = Lang.Get("canevents:admin-hg-help-text");
            double width = column.fixedWidth - Card.Padding * 2;
            double height = ButtonRow.MultiLineHeight(text, width) + 4;

            var inner = Section.Begin(ctx, column, y, "hg-help", Lang.Get("canevents:admin-hg-help-title"),
                height, out double nextY, defaultOpen: false);
            if (inner != null)
                ctx.Compo.AddStaticText(text, AdminFonts.Hint, inner.FlatCopy().WithFixedHeight(height), "hg-help-text");
            return nextY;
        }

        private void Save(AdminPanelGui gui, PlaceListEntryMsg place, HgSetupMsg s, string[] codes)
        {
            string loot = buf.LootToJson();
            string phases = buf.PhasesToJson();
            string scoreboard = buf.ScoreboardVisibility;
            bool all = buf.CfgApplyToAll;
            string target = all ? string.Join(", ", codes) : s.EventCode;
            gui.Act(Lang.Get("canevents:admin-hg-log-save", target ?? "?"),
                () => gui.Net.SendSaveHgConfig(s.EventCode, loot, phases, place.Id, all, scoreboard));
            buf.MarkSaved();
            gui.RequestRebuild();
        }


        // Returns the x where the field starts.
        private static double AddLabel(PageBuildContext ctx, string text, double x, double y, string key)
        {
            var font = CairoFont.WhiteSmallText().WithColor(AdminColors.Label);
            double w = font.GetTextExtents(text).Width + 6;
            ctx.Compo.AddStaticText(text, font, ElementBounds.Fixed(x, y + 5, w, LineHeight), key);
            return x + w;
        }

        // Highlight ints are packed with red in the low byte.
        private static double[] TierColor(int tierIdx)
        {
            int c = ZoneVisualizer.LootTierColor(tierIdx);
            return new[] { (c & 0xFF) / 255.0, ((c >> 8) & 0xFF) / 255.0, ((c >> 16) & 0xFF) / 255.0, 1.0 };
        }
    }
}
