using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventPlacesManagers.Places;
using canevents.src.Gui.Admin.Dialogs;
using canevents.src.Gui.Admin.GuiElements;
using canevents.src.Gui.Admin.Pages.Sections;
using canevents.src.Gui.Admin.Widgets;
using canevents.src.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace canevents.src.Gui.Admin.Pages
{
    // Place and Modes go in a ScrollPane: MM section alone is ~1000 px vs a 520 px content area.
    public sealed partial class ArenasPage : AdminPageBase
    {
        private const int SubList = 0;
        private const int SubPlace = 1;
        private const int SubModes = 2;

        private const string PlacePaneKey = "ar-place-pane";
        private const string ModesPaneKey = "ar-modes-pane";

        private const double LineHeight = 20;
        private const double InputHeight = 26;

        private static readonly string[] SubKeys =
        {
            "canevents:admin-arenas-sub-list",
            "canevents:admin-arenas-sub-place",
            "canevents:admin-arenas-sub-modes"
        };

        // Their /canep commands find the place by player position, so bounds are required.
        internal static readonly string[] BoundsRequiringTypes =
        {
            PlaceTags.Spleef, PlaceTags.Parkour, PlaceTags.Koth, PlaceTags.HungerGames, PlaceTags.Ctf
        };

        protected override PanelTab Tab => PanelTab.Arenas;
        protected override string[] SubTabLangKeys => SubKeys;

        private long selectedPlaceId = -1;

        private readonly HashSet<long> bulkIds = new HashSet<long>();
        private PlaceListEntryMsg[] bulkPrunedFor;
        private int bulkTypeIdx;

        private readonly MurderMysterySection mm = new MurderMysterySection();
        private readonly HungerGamesSection hg = new HungerGamesSection();

        // Composer element keys must be unique per build.
        private int keySeq;
        private string K(string prefix) => prefix + "-" + (keySeq++);

        public override void Watch(PageWatchList w)
        {
            w.Add(Net.PlaceList).Add(Net.AdminEventList).Add(Net.PlatformEditState).Add(Net.PadList);
            mm.Watch(w, Net);
            hg.Watch(w, Net);
        }

        public override void OnShown()
        {
            // Place tooltip lists which events could run there.
            Net.RequestAdminEventList();
            Net.SendPadEdit("list");
            hg.OnShown();
            mm.OnShown();
        }

        public override void OnHidden()
        {
            hg.OnHidden();
            ScrollPane.Remove(Gui, PlacePaneKey);
            ScrollPane.Remove(Gui, ModesPaneKey);
        }

        public override bool WantsPlacementMarker(out int tierIdx)
        {
            tierIdx = -1;
            if (SubTab != SubModes) return false;
            var place = Highlighter.FindPlace(selectedPlaceId);
            if (!HungerGamesSection.AppliesTo(place)) return false;
            return hg.WantsPlacementMarker(out tierIdx);
        }

        protected override void OnSubTabChanged(int subTab)
        {
            // The sub-tab click never reaches the pane composer, so a focused field there would block the rebuild.
            ScrollPane.Unfocus(Gui, PlacePaneKey);
            ScrollPane.Unfocus(Gui, ModesPaneKey);
        }

        protected override void BuildSubTab(PageBuildContext ctx, int subTab)
        {
            keySeq = 0;
            ScrollPane.Remove(Gui, PlacePaneKey);
            ScrollPane.Remove(Gui, ModesPaneKey);

            var places = Net.PlaceList.Value?.Places;
            PruneBulk(places);

            var place = Highlighter.FindPlace(selectedPlaceId);
            if (place == null && places != null) selectedPlaceId = -1;

            Highlighter.Update(place);

            switch (subTab)
            {
                case SubPlace:
                    if (place == null) BuildNoSelection(ctx);
                    else BuildInPane(ctx, PlacePaneKey, pctx => BuildPlaceTab(pctx, place));
                    break;
                case SubModes:
                    if (place == null) BuildNoSelection(ctx);
                    else BuildInPane(ctx, ModesPaneKey, pctx => BuildModesTab(pctx, place));
                    break;
                default:
                    BuildList(ctx, places);
                    break;
            }
        }

        private void BuildInPane(PageBuildContext ctx, string paneKey, System.Func<PageBuildContext, double> build)
        {
            double top = ctx.Current.fixedY;
            var viewport = ElementBounds.Fixed(ctx.Main.fixedX, top, ctx.Width, ctx.Bottom - top);
            var pane = ScrollPane.Begin(ctx, viewport, paneKey);
            double y = build(pane.Ctx);
            pane.End(y);
        }

        private void BuildNoSelection(PageBuildContext ctx)
        {
            var column = ctx.Column(0, ctx.Width);
            double y = ctx.Current.fixedY + 4;
            string hint = Lang.Get("canevents:admin-arenas-no-selection");
            double width = column.fixedWidth - Card.Padding * 2;
            double hintHeight = ButtonRow.TextHeight(hint, width);
            double height = Card.HeightAround(hintHeight + 6 + ButtonRow.ButtonExtent);

            var inner = Card.Frame(ctx.Compo, column, y, height, Lang.Get("canevents:admin-arenas-no-selection-title"));
            ctx.Compo.AddStaticText(hint, AdminFonts.Hint, inner.FlatCopy().WithFixedHeight(hintHeight), "ar-nosel-hint");

            var row = new ButtonRow(ctx.Compo, ElementBounds.Fixed(inner.fixedX, inner.fixedY + hintHeight + 6, width, 0), width, 0, "ar-nosel-btn");
            row.Add(Lang.Get("canevents:admin-arenas-to-list"), () =>
            {
                SubTab = SubList;
                Rebuild();
            });
        }

        private void BuildList(PageBuildContext ctx, PlaceListEntryMsg[] places)
        {
            var compo = ctx.Compo;
            var column = ctx.Column(0, ctx.Width);
            double width = column.fixedWidth - Card.Padding * 2;
            double y = ctx.Current.fixedY + 2;

            string legend = Lang.Get("canevents:admin-arenas-legend");
            double legendHeight = ButtonRow.TextHeight(legend, width);

            string[] tags = PlaceTags.Known;
            if (bulkTypeIdx < 0 || bulkTypeIdx >= tags.Length) bulkTypeIdx = 0;
            string tag = tags[bulkTypeIdx];
            bool havePlaces = places != null && places.Length > 0;
            // Select all / Select this type only act on filtered rows.
            var shown = havePlaces ? ApplyFilters(places) : places;
            int selected = bulkIds.Count;
            int hiddenSelected = havePlaces ? selected - shown.Count(p => bulkIds.Contains(p.Id)) : 0;

            var filterRow = havePlaces ? MeasureFilterRow(shown.Length, places.Count(p => p != null)) : null;
            double filterRowHeight = filterRow != null ? filterRow.Height(width) + 6 : 0;

            string bulkLabel = Lang.Get("canevents:admin-arenas-bulk-label");
            double bulkLabelWidth = AdminFonts.Body.GetTextExtents(bulkLabel).Width + 10;
            const double typeWidth = 150;
            string selectAll = Lang.Get("canevents:admin-arenas-bulk-select-all");
            string selectType = Lang.Get("canevents:admin-arenas-bulk-select-type");
            string clearSel = Lang.Get("canevents:admin-arenas-bulk-clear");

            // Bulk actions also hit selected rows hidden by filters.
            string countLabel = hiddenSelected > 0
                ? Lang.Get("canevents:admin-arenas-bulk-count-hidden", selected, hiddenSelected)
                : Lang.Get("canevents:admin-arenas-bulk-count", selected);
            double countWidth = AdminFonts.Body.GetTextExtents(countLabel).Width + 10;
            string enableCap = Lang.Get("canevents:admin-arenas-bulk-enable");
            string disableCap = Lang.Get("canevents:admin-arenas-bulk-disable");
            string addCap = Lang.Get("canevents:admin-arenas-bulk-add-type", tag);
            string removeCap = Lang.Get("canevents:admin-arenas-bulk-remove-type", tag);
            string deleteCap = Lang.Get("canevents:admin-arenas-bulk-delete");
            string skipped = Lang.Get("canevents:admin-arenas-bulk-skipped");

            // Measure with all buttons, the row can wrap.
            double selectRowHeight = havePlaces
                ? (selected > 0
                    ? ButtonRow.HeightForWidths(width, bulkLabelWidth, ButtonRow.ButtonWidth(selectAll), typeWidth,
                        ButtonRow.ButtonWidth(selectType), ButtonRow.ButtonWidth(clearSel))
                    : ButtonRow.HeightForWidths(width, bulkLabelWidth, ButtonRow.ButtonWidth(selectAll), typeWidth,
                        ButtonRow.ButtonWidth(selectType))) + 6
                : 0;
            double actionHeight = havePlaces && selected > 0
                ? ButtonRow.HeightFor(width, countWidth, enableCap, disableCap, addCap, removeCap, deleteCap) + LineHeight + 4
                : 0;
            double cardHeight = Card.HeightAround(legendHeight + 6 + filterRowHeight + selectRowHeight + actionHeight);

            var inner = Card.Frame(compo, column, y, cardHeight, Lang.Get("canevents:admin-arenas-title"));
            AddHeaderButtons(ctx, column, y);

            double cy = inner.fixedY;
            compo.AddStaticText(legend, AdminFonts.Hint, ElementBounds.Fixed(inner.fixedX, cy, width, legendHeight), "ar-legend");
            cy += legendHeight + 6;

            if (havePlaces)
            {
                BuildFilterRow(ctx, filterRow, inner.fixedX, cy, width);
                cy += filterRowHeight;

                compo.AddStaticText(bulkLabel, AdminFonts.Body, ElementBounds.Fixed(inner.fixedX, cy + 6, bulkLabelWidth, LineHeight), "ar-bulk-label");
                var row = new ButtonRow(compo, ElementBounds.Fixed(inner.fixedX, cy, width, 0), width, bulkLabelWidth, "ar-bulk-sel");
                row.Add(selectAll, () =>
                {
                    foreach (var p in shown) bulkIds.Add(p.Id);
                    Rebuild();
                }, Lang.Get("canevents:admin-arenas-bulk-select-all-tip"));

                var typeBounds = row.Next(typeWidth);
                compo.AddDropDown(tags, tags, bulkTypeIdx, (value, on) =>
                {
                    int i = Array.IndexOf(tags, value);
                    if (i >= 0) bulkTypeIdx = i;
                    Rebuild();
                }, typeBounds, "ar-bulk-type");
                Tooltip.Add(compo, Lang.Get("canevents:admin-arenas-bulk-type-tip"), typeBounds, "tip-ar-bulk-type");

                row.Add(selectType, () =>
                {
                    foreach (var p in shown)
                        if (PlaceHighlighter.HasType(p, tag)) bulkIds.Add(p.Id);
                    Rebuild();
                }, Lang.Get("canevents:admin-arenas-bulk-select-type-tip"));
                if (selected > 0)
                {
                    row.Add(clearSel, () =>
                    {
                        bulkIds.Clear();
                        Rebuild();
                    });
                }
                cy += selectRowHeight;

                if (selected > 0)
                {
                    compo.AddStaticText(countLabel, CairoFont.WhiteSmallText().WithColor(AdminColors.Section),
                        ElementBounds.Fixed(inner.fixedX, cy + 6, countWidth, LineHeight), "ar-bulk-count");
                    var actions = new ButtonRow(compo, ElementBounds.Fixed(inner.fixedX, cy, width, 0), width, countWidth, "ar-bulk-act");
                    actions.Add(enableCap, () => SendBulk(BulkEnabledMode.Enable, null, null));
                    actions.Add(disableCap, () => SendBulk(BulkEnabledMode.Disable, null, null));
                    actions.Add(addCap, () => SendBulk(BulkEnabledMode.Leave, tag, null),
                        Lang.Get("canevents:admin-arenas-bulk-add-type-tip"));
                    actions.Add(removeCap, () => SendBulk(BulkEnabledMode.Leave, null, tag),
                        Lang.Get("canevents:admin-arenas-bulk-remove-type-tip"));
                    actions.Add(deleteCap, () => OpenBulkDelete(), Lang.Get("canevents:admin-arenas-bulk-delete-tip"));
                    double actionsBottom = actions.Bounds.fixedY + ButtonRow.ButtonExtent;

                    compo.AddStaticText(Card.Fit(skipped, AdminFonts.Hint, width), AdminFonts.Hint,
                        ElementBounds.Fixed(inner.fixedX, actionsBottom + 2, width, LineHeight), "ar-bulk-skipped");
                }
            }

            y += cardHeight + Card.Gap;

            if (!havePlaces)
            {
                compo.AddStaticText(Lang.Get(places == null ? "canevents:admin-arenas-loading" : "canevents:admin-arenas-empty"),
                    AdminFonts.Hint, ElementBounds.Fixed(ctx.Main.fixedX, y, ctx.Width, 22), "ar-empty");
                return;
            }

            if (shown.Length == 0)
            {
                compo.AddStaticText(Lang.Get("canevents:admin-arenas-filter-none"),
                    AdminFonts.Hint, ElementBounds.Fixed(ctx.Main.fixedX, y, ctx.Width, 22), "ar-filter-none");
                return;
            }

            BuildTable(ctx, shown, y);
        }

        private void AddHeaderButtons(PageBuildContext ctx, ElementBounds column, double cardY)
        {
            string refresh = Lang.Get("canevents:admin-arenas-refresh");
            string create = Lang.Get("canevents:admin-arenas-new");
            double rw = ButtonRow.ButtonWidth(refresh) - 10;
            double cw = ButtonRow.ButtonWidth(create) - 10;
            double right = column.fixedX + column.fixedWidth - Card.Padding;

            var rb = ElementBounds.Fixed(right - rw, cardY + Card.Padding, rw, 20);
            ctx.Compo.AddSmallButton(refresh, new ActionConsumable(() =>
            {
                Net.RequestPlaceList();
                Net.RequestAdminEventList();
                return true;
            }), rb, EnumButtonStyle.Small, "ar-refresh");

            var cb = ElementBounds.Fixed(rb.fixedX - 8 - cw, cardY + Card.Padding, cw, 20);
            ctx.Compo.AddSmallButton(create, new ActionConsumable(() =>
            {
                GoTo(PanelTab.NewArena);
                return true;
            }), cb, EnumButtonStyle.Small, "ar-new");
            Tooltip.Add(ctx.Compo, Lang.Get("canevents:admin-arenas-new-tip"), cb, "tip-ar-new");
        }

        private void BuildTable(PageBuildContext ctx, PlaceListEntryMsg[] places, double y)
        {
            var spec = new ColumnSpec(
                new ColumnDef(null, 30),
                new ColumnDef(Lang.Get("canevents:admin-arenas-col-id"), 40),
                new ColumnDef(Lang.Get("canevents:admin-arenas-col-name"), 0),
                new ColumnDef(Lang.Get("canevents:admin-arenas-col-types"), 140),
                new ColumnDef(Lang.Get("canevents:admin-arenas-col-size"), 70),
                new ColumnDef(Lang.Get("canevents:admin-arenas-col-spawns"), 30, EnumTextOrientation.Right),
                new ColumnDef(Lang.Get("canevents:admin-arenas-col-setup"), 90),
                new ColumnDef(Lang.Get("canevents:admin-arenas-col-status"), 60),
                new ColumnDef(null, 70));

            var rows = new List<ColumnRow>(places.Length);
            foreach (var place in places)
            {
                if (place == null) continue;
                rows.Add(BuildRow(place));
            }

            var anchor = ElementBounds.Fixed(ctx.Main.fixedX, y, ctx.Width, 0);
            double listTop = y + ColumnTable.HeaderHeight + 4;
            double listHeight = Math.Max(60, ctx.Bottom - listTop - 10);
            ColumnTable.Add(ctx, anchor, spec, rows, listHeight, new ScrollableListOptions { Key = PlacesListKey });
        }

        private ColumnRow BuildRow(PlaceListEntryMsg place)
        {
            long id = place.Id;
            bool inBulk = bulkIds.Contains(id);

            string status;
            double[] statusColor;
            if (place.Occupied) { status = Lang.Get("canevents:admin-arenas-status-inuse"); statusColor = AdminColors.Danger; }
            else if (!place.Enabled) { status = Lang.Get("canevents:admin-arenas-status-off"); statusColor = AdminColors.Muted; }
            else { status = Lang.Get("canevents:admin-arenas-status-free"); statusColor = AdminColors.Success; }

            SetupBadges(place, out string badges, out double[] badgeColor, out string badgeTip);

            var row = new ColumnRow(null, id.ToString(), place.Name ?? "",
                    place.Types != null ? string.Join(", ", place.Types) : "",
                    FormatBoundsSize(place), place.SpawnCount.ToString(), badges, status, null)
                .WithColor(1, AdminColors.Muted)
                .WithColor(2, place.Enabled ? AdminColors.Value : AdminColors.Muted)
                .WithColor(3, AdminColors.Label)
                .WithColor(4, AdminColors.Muted)
                .WithColor(5, AdminColors.Label)
                .WithColor(6, badgeColor)
                .WithColor(7, statusColor)
                .WithCellTooltip(2, BuildPlaceTooltip(place))
                .WithCellTooltip(6, badgeTip);

            row.Selected = id == selectedPlaceId || inBulk;

            row.WithButton(0, inBulk ? "[x]" : "[ ]", () =>
            {
                if (!bulkIds.Remove(id)) bulkIds.Add(id);
                Rebuild();
            }, Lang.Get("canevents:admin-arenas-row-box-tip"), 26);

            row.WithButton(8, Lang.Get("canevents:admin-arenas-row-open"), () =>
            {
                selectedPlaceId = id;
                SubTab = SubPlace;
                Rebuild();
            }, Lang.Get("canevents:admin-arenas-row-open-tip"));

            // Ctrl+click toggles bulk selection.
            row.OnClick = () =>
            {
                if (CtrlHeld())
                {
                    if (!bulkIds.Remove(id)) bulkIds.Add(id);
                }
                else
                {
                    selectedPlaceId = id;
                }
                Rebuild();
            };
            return row;
        }

        private bool CtrlHeld()
        {
            var keys = Capi.Input.KeyboardKeyStateRaw;
            if (keys == null) return false;
            int l = (int)GlKeys.ControlLeft, r = (int)GlKeys.ControlRight;
            return (l < keys.Length && keys[l]) || (r < keys.Length && keys[r]);
        }

        // One letter per requirement, "!" if missing. A cell has one colour, so it takes the worst badge.
        private void SetupBadges(PlaceListEntryMsg place, out string text, out double[] color, out string tip)
        {
            var letters = new List<string>();
            var lines = new List<string>();
            int worst = 0; // 0 ok, 1 warning, 2 missing

            void Badge(string letter, bool ok, string line, bool optional = false)
            {
                letters.Add(ok ? letter : letter + "!");
                lines.Add(letter + ": " + line);
                if (!ok) worst = Math.Max(worst, optional ? 1 : 2);
            }

            Badge("S", place.SpawnCount > 0, Lang.Get("canevents:admin-arenas-badge-spawns", place.SpawnCount));

            bool needsBounds = place.Types != null && place.Types.Any(t => BoundsRequiringTypes.Contains(t));
            if (needsBounds)
                Badge("B", place.HasBounds, Lang.Get(place.HasBounds ? "canevents:admin-arenas-badge-bounds" : "canevents:admin-arenas-badge-nobounds"));
            if (PlaceHighlighter.HasType(place, PlaceTags.Koth))
                Badge("H", HasKothHill(place),
                    HasKothHill(place) ? place.KothHill : Lang.Get("canevents:admin-arenas-badge-nohill"));
            if (PlaceHighlighter.HasType(place, PlaceTags.Parkour))
                Badge("C", HasParkourCheckpoints(place), Lang.Get("canevents:admin-arenas-badge-checkpoints"));
            if (PlaceHighlighter.HasType(place, PlaceTags.HungerGames))
                Badge("Z", HasHgZone(place), Lang.Get("canevents:admin-arenas-badge-zone"));
            if (PlaceHighlighter.HasType(place, PlaceTags.Spleef))
                Badge("P", HasSpleefPlatform(place), Lang.Get("canevents:admin-arenas-badge-platform"));
            // Optional: MM plays fine without weapon spots.
            if (PlaceHighlighter.HasType(place, PlaceTags.MurderMystery))
            {
                int spots = PlaceHighlighter.MmPickupCount(place);
                Badge("W", spots > 0, spots > 0
                    ? Lang.Get("canevents:admin-arenas-badge-weapons", spots)
                    : Lang.Get("canevents:admin-arenas-badge-noweapons"), optional: true);
            }

            // Server-side PlaceSetupCheck gaps, e.g. CTF posts, which have no letter.
            if (HasSetupGaps(place))
            {
                if (!letters.Any(l => l.EndsWith("!"))) letters.Add("!");
                foreach (var gap in place.SetupGaps) lines.Add("! " + gap);
                worst = 2;
            }

            text = string.Join(" ", letters);
            color = worst == 2 ? AdminColors.Danger : worst == 1 ? AdminColors.Warning : AdminColors.Success;
            tip = string.Join("\n", lines);
        }

        private string BuildPlaceTooltip(PlaceListEntryMsg place)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(place.Name).Append(" (#").Append(place.Id).Append(')');
            if (!string.IsNullOrWhiteSpace(place.Description)) sb.Append('\n').Append(place.Description);

            sb.Append('\n').Append(place.HasBounds
                ? Lang.Get("canevents:admin-arenas-bounds-line", BoundsCorners(place), FormatBoundsSize(place))
                : Lang.Get("canevents:admin-arenas-tip-nobounds"));

            sb.Append('\n').Append(SourceLine(place));

            if (place.BehaviorSummary != null)
                foreach (var line in place.BehaviorSummary) sb.Append('\n').Append(line);

            var serves = Net.AdminEventList.Value?.Events?
                .Where(e => e.EventPlaceTypes != null && e.EventPlaceTypes.Length > 0
                            && e.EventPlaceTypes.All(t => place.Types != null && Array.IndexOf(place.Types, t) >= 0))
                .Select(e => e.Code)
                .ToArray();
            if (serves != null && serves.Length > 0)
                sb.Append('\n').Append(Lang.Get("canevents:admin-arenas-tip-serves", string.Join(", ", serves)));

            return sb.ToString();
        }

        // Runs once per new list snapshot.
        private void PruneBulk(PlaceListEntryMsg[] places)
        {
            if (places == null || ReferenceEquals(places, bulkPrunedFor)) return;
            bulkPrunedFor = places;
            var present = new HashSet<long>(places.Where(p => p != null).Select(p => p.Id));
            bulkIds.IntersectWith(present);
        }

        private void SendBulk(BulkEnabledMode mode, string addType, string removeType)
        {
            var ids = bulkIds.ToArray();
            int n = ids.Length;
            if (n == 0) return;

            string log;
            if (addType != null) log = Lang.Get("canevents:admin-arenas-log-bulk-add", addType, n);
            else if (removeType != null) log = Lang.Get("canevents:admin-arenas-log-bulk-remove", removeType, n);
            else if (mode == BulkEnabledMode.Enable) log = Lang.Get("canevents:admin-arenas-log-bulk-enable", n);
            else log = Lang.Get("canevents:admin-arenas-log-bulk-disable", n);

            Act(log, () => Net.SendBulkEditPlaces(ids, mode, addType, removeType));
        }

        private void OpenBulkDelete()
        {
            var places = Net.PlaceList.Value?.Places;
            var chosen = places == null ? new PlaceListEntryMsg[0]
                : places.Where(p => p != null && bulkIds.Contains(p.Id)).ToArray();
            if (chosen.Length == 0) return;

            var names = chosen.Select(p => (p.Name ?? "?") + " #" + p.Id).ToArray();
            string list = names.Length <= 8
                ? string.Join("\n", names)
                : string.Join("\n", names.Take(8)) + "\n" + Lang.Get("canevents:admin-arenas-delete-more", names.Length - 8);
            string question = Lang.Get("canevents:admin-arenas-bulk-delete-question", names.Length) + "\n\n" + list;

            var ids = chosen.Select(p => p.Id).ToArray();
            Gui.OpenSideDialog(new ConfirmPanel(Lang.Get("canevents:admin-arenas-bulk-delete-title"), question, () =>
            {
                Act(Lang.Get("canevents:admin-arenas-log-bulk-delete", ids.Length), () => Net.SendBulkDeletePlaces(ids));
                if (Array.IndexOf(ids, selectedPlaceId) >= 0) selectedPlaceId = -1;
                bulkIds.Clear();
                Rebuild();
            }, "canevents:admin-arenas-delete-confirm"));
        }

        internal static string FormatBoundsSize(PlaceListEntryMsg place)
        {
            if (!place.HasBounds) return "-";
            return string.Format("{0}x{1}x{2}",
                place.BoundsMaxX - place.BoundsMinX + 1,
                place.BoundsMaxY - place.BoundsMinY + 1,
                place.BoundsMaxZ - place.BoundsMinZ + 1);
        }

        private static string BoundsCorners(PlaceListEntryMsg place)
            => string.Format("{0},{1},{2} .. {3},{4},{5}",
                place.BoundsMinX, place.BoundsMinY, place.BoundsMinZ,
                place.BoundsMaxX, place.BoundsMaxY, place.BoundsMaxZ);

        private static string SourceLine(PlaceListEntryMsg place)
        {
            if (string.IsNullOrEmpty(place.SchematicTemplateCode)) return Lang.Get("canevents:admin-arenas-source-world");
            return Lang.Get(place.SchematicOnDemand ? "canevents:admin-arenas-source-template-ondemand" : "canevents:admin-arenas-source-template",
                place.SchematicTemplateCode);
        }

        // Setup checks use structured fields only, BehaviorSummary is display text.
        internal static bool HasSetupGaps(PlaceListEntryMsg place) => place.SetupGaps != null && place.SetupGaps.Length > 0;

        internal static bool HasKothHill(PlaceListEntryMsg place) => !string.IsNullOrEmpty(place.KothHill);

        internal static bool HasParkourCheckpoints(PlaceListEntryMsg place)
            => place.ParkourCheckpoints != null && place.ParkourCheckpoints.Length > 0;

        // Without it the server uses the bounds centre.
        internal static bool HasHgZone(PlaceListEntryMsg place) => place.HgZoneSet;

        internal static bool HasSpleefPlatform(PlaceListEntryMsg place) => place.SpleefPlatformBlocks > 0;

        // Ellipsized, full text on hover.
        private void Line(PageBuildContext ctx, double x, double y, double width, string text, double[] color)
        {
            var font = CairoFont.WhiteSmallText().WithColor(color ?? AdminColors.Value);
            Card.AddFitLine(ctx.Compo, text, font, ElementBounds.Fixed(x, y, width, LineHeight),
                K("ar-line"), K("ar-line-tip"));
        }

        private double Wrapped(PageBuildContext ctx, double x, double y, double width, string text, CairoFont font)
        {
            double h = ButtonRow.MultiLineHeight(text, width);
            ctx.Compo.AddStaticText(text, font, ElementBounds.Fixed(x, y, width, h), K("ar-wrap"));
            return h;
        }

        private ElementBounds Button(PageBuildContext ctx, ButtonRow row, string caption, Action onClick,
                                     string tooltip = null, bool enabled = true)
        {
            var b = row.Next(ButtonRow.ButtonWidth(caption));
            string key = K("ar-btn");
            ctx.Compo.AddButton(caption, new ActionConsumable(() => { onClick(); return true; }), b, EnumButtonStyle.Normal, key);
            if (!enabled) ctx.Compo.GetButton(key).Enabled = false;
            if (tooltip != null) Tooltip.Add(ctx.Compo, tooltip, b, "tip-" + key);
            return b;
        }

        private double FieldLabel(PageBuildContext ctx, double x, double y, string text)
        {
            double w = AdminFonts.Body.GetTextExtents(text).Width + 8;
            ctx.Compo.AddStaticText(text, AdminFonts.Body, ElementBounds.Fixed(x, y + 4, w, LineHeight), K("ar-flabel"));
            return w;
        }

        // For buttons that use the block under the player's feet. Returns 0 when inside or no bounds.
        private double OutsideWarning(PageBuildContext ctx, PlaceListEntryMsg place, double x, double y, double width,
                                      string whatGoesOffLangKey, bool measureOnly = false)
        {
            if (!place.HasBounds || Highlighter.PlayerInsideBounds(place)) return 0;
            string text = Lang.Get("canevents:admin-arenas-outside") + " " + Lang.Get(whatGoesOffLangKey);
            double h = ButtonRow.TextHeight(text, width) + 4;
            if (!measureOnly)
            {
                ctx.Compo.AddStaticText(text, CairoFont.WhiteSmallText().WithColor(AdminColors.Warning),
                    ElementBounds.Fixed(x, y, width, h - 4), K("ar-outside"));
            }
            return h;
        }
    }
}
