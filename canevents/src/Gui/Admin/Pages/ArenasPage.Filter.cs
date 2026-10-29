using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using canevents.src.EventPlacesManagers.Places;
using canevents.src.Gui.Admin.Widgets;
using canevents.src.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace canevents.src.Gui.Admin.Pages
{
    // Filter text never rebuilds per keystroke: a rebuild after focus loss would land between
    // mouse-down and mouse-up and eat the click. Enter isn't caught (no key handler on the panel).
    public sealed partial class ArenasPage
    {
        private const string FilterTextKey = "ar-filter-text";
        private const string PlacesListKey = "ar-places";

        private const double FilterFieldWidth = 160;
        private const double FilterTagWidth = 130;
        private const double FilterStatusWidth = 150;

        // Index-aligned with the status dropdown; lang key suffixes.
        private const int StatusAll = 0, StatusReady = 1, StatusNotSetUp = 2, StatusNoSpawns = 3,
                          StatusDisabled = 4, StatusInUse = 5;
        private static readonly string[] StatusCodes = { "all", "ready", "notsetup", "nospawns", "disabled", "inuse" };

        private string filterText = "";
        // 0 = any tag, else PlaceTags.Known[i - 1].
        private int filterTagIdx;
        private int filterStatusIdx;
        // A change resets the table scroll.
        private string lastFilterSig;

        private string FilterQuery => (filterText ?? "").Trim();

        private bool Filtered => FilterQuery.Length > 0 || filterTagIdx > 0 || filterStatusIdx > 0;

        private PlaceListEntryMsg[] ApplyFilters(PlaceListEntryMsg[] places)
        {
            if (filterTagIdx < 0 || filterTagIdx > PlaceTags.Known.Length) filterTagIdx = 0;
            if (filterStatusIdx < 0 || filterStatusIdx >= StatusCodes.Length) filterStatusIdx = 0;

            string sig = FilterQuery.ToLowerInvariant() + "|" + filterTagIdx + "|" + filterStatusIdx;
            if (lastFilterSig != null && sig != lastFilterSig) State.Scroll[PlacesListKey] = 0f;
            lastFilterSig = sig;

            return places.Where(p => p != null && MatchesFilters(p)).ToArray();
        }

        private bool MatchesFilters(PlaceListEntryMsg p)
        {
            string q = FilterQuery;
            if (q.Length > 0)
            {
                // "#12" matches id 12.
                string idQuery = q.TrimStart('#');
                bool byName = (p.Name ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
                bool byId = idQuery.Length > 0 && p.Id.ToString(CultureInfo.InvariantCulture).Contains(idQuery);
                if (!byName && !byId) return false;
            }

            if (filterTagIdx > 0 && !PlaceHighlighter.HasType(p, PlaceTags.Known[filterTagIdx - 1])) return false;

            switch (filterStatusIdx)
            {
                case StatusReady: return p.Enabled && p.SpawnCount > 0 && !HasSetupGaps(p);
                case StatusNotSetUp: return HasSetupGaps(p);
                case StatusNoSpawns: return p.SpawnCount <= 0;
                case StatusDisabled: return !p.Enabled;
                case StatusInUse: return p.Occupied;
                default: return true;
            }
        }

        private void ClearFilters()
        {
            filterText = "";
            filterTagIdx = 0;
            filterStatusIdx = StatusAll;
            State.Drafts.Remove(FilterTextKey);
        }

        // Measured before the card is framed. Widths follow BuildFilterRow's control order.
        private sealed class FilterRowLayout
        {
            public string Label, Apply, Count, Clear;
            public double LabelWidth, CountWidth;
            public double[] Widths;

            public double Height(double width) => ButtonRow.HeightForWidths(width, LabelWidth, Widths);
        }

        private static CairoFont FilterCountFont => CairoFont.WhiteSmallText().WithColor(AdminColors.Section);

        private FilterRowLayout MeasureFilterRow(int shown, int total)
        {
            var r = new FilterRowLayout
            {
                Label = Lang.Get("canevents:admin-arenas-filter-label"),
                Apply = Lang.Get("canevents:admin-filter")
            };
            r.LabelWidth = AdminFonts.Body.GetTextExtents(r.Label).Width + 10;

            var widths = new List<double> { FilterFieldWidth, ButtonRow.ButtonWidth(r.Apply), FilterTagWidth, FilterStatusWidth };
            if (Filtered)
            {
                r.Count = Lang.Get("canevents:admin-arenas-filter-count", shown, total);
                r.CountWidth = FilterCountFont.GetTextExtents(r.Count).Width + 10;
                r.Clear = Lang.Get("canevents:admin-arenas-filter-clear");
                widths.Add(r.CountWidth);
                widths.Add(ButtonRow.ButtonWidth(r.Clear));
            }
            r.Widths = widths.ToArray();
            return r;
        }

        private void BuildFilterRow(PageBuildContext ctx, FilterRowLayout r, double x, double y, double width)
        {
            var compo = ctx.Compo;
            compo.AddStaticText(r.Label, AdminFonts.Body, ElementBounds.Fixed(x, y + 6, r.LabelWidth, LineHeight), "ar-filter-label");
            var row = new ButtonRow(compo, ElementBounds.Fixed(x, y, width, 0), width, r.LabelWidth, "ar-filter");

            var field = row.Next(FilterFieldWidth).WithFixedHeight(InputHeight);
            field.fixedY += 1;
            NumberField.AddText(ctx, field, FilterTextKey, () => filterText, v => filterText = v ?? "",
                Lang.Get("canevents:admin-arenas-filter-text-tip"));

            row.Add(r.Apply, () => Rebuild(), Lang.Get("canevents:admin-arenas-filter-apply-tip"));

            string[] tags = PlaceTags.Known;
            var tagValues = new string[tags.Length + 1];
            var tagNames = new string[tags.Length + 1];
            tagValues[0] = "";
            tagNames[0] = Lang.Get("canevents:admin-arenas-filter-tag-all");
            for (int i = 0; i < tags.Length; i++)
            {
                tagValues[i + 1] = tags[i];
                tagNames[i + 1] = tags[i];
            }
            var tagBounds = row.Next(FilterTagWidth);
            compo.AddDropDown(tagValues, tagNames, filterTagIdx, (value, on) =>
            {
                int i = Array.IndexOf(tagValues, value);
                if (i >= 0) filterTagIdx = i;
                Rebuild();
            }, tagBounds, "ar-filter-tag");
            Tooltip.Add(compo, Lang.Get("canevents:admin-arenas-filter-tag-tip"), tagBounds, "tip-ar-filter-tag");

            var statusNames = StatusCodes.Select(c => Lang.Get("canevents:admin-arenas-filter-status-" + c)).ToArray();
            var statusBounds = row.Next(FilterStatusWidth);
            compo.AddDropDown(StatusCodes, statusNames, filterStatusIdx, (value, on) =>
            {
                int i = Array.IndexOf(StatusCodes, value);
                if (i >= 0) filterStatusIdx = i;
                Rebuild();
            }, statusBounds, "ar-filter-status");
            Tooltip.Add(compo, Lang.Get("canevents:admin-arenas-filter-status-tip"), statusBounds, "tip-ar-filter-status");

            if (r.Count != null)
            {
                var countBounds = row.Next(r.CountWidth);
                compo.AddStaticText(r.Count, FilterCountFont,
                    ElementBounds.Fixed(countBounds.fixedX, countBounds.fixedY + 6, r.CountWidth, LineHeight), "ar-filter-count");
                row.Add(r.Clear, () =>
                {
                    ClearFilters();
                    Rebuild();
                });
            }
        }
    }
}
