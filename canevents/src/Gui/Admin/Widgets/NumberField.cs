using System;
using canevents.src.Gui.Admin.Pages;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace canevents.src.Gui.Admin.Widgets
{
    // No clamping while typing ("5" on the way to "50"); invalid text waits in AdminGuiState.Drafts.
    public static class NumberField
    {
        public const double DefaultHeight = 26;

        public static ElementBounds Add(PageBuildContext ctx, ElementBounds bounds, string key,
                                        Func<double> get, Action<double> set,
                                        double min = double.MinValue, double max = double.MaxValue,
                                        string tooltip = null)
            => AddCore(ctx, bounds, key, InputKind.Decimal,
                () => NumberParse.Format(get()),
                text =>
                {
                    if (!NumberParse.TryDouble(text, out double v) || v < min || v > max) return false;
                    set(v);
                    return true;
                },
                RangeTooltip(tooltip, min, max, double.MinValue, double.MaxValue));

        public static ElementBounds AddInt(PageBuildContext ctx, ElementBounds bounds, string key,
                                           Func<int> get, Action<int> set,
                                           int min = int.MinValue, int max = int.MaxValue,
                                           string tooltip = null)
            => AddCore(ctx, bounds, key, InputKind.Integer,
                () => NumberParse.Format(get()),
                text =>
                {
                    if (!NumberParse.TryInt(text, out int v) || v < min || v > max) return false;
                    set(v);
                    return true;
                },
                RangeTooltip(tooltip, min, max, int.MinValue, int.MaxValue));

        public static ElementBounds AddText(PageBuildContext ctx, ElementBounds bounds, string key,
                                            Func<string> get, Action<string> set, string tooltip = null)
            => AddCore(ctx, bounds, key, InputKind.Text,
                () => get() ?? "",
                text => { set(text ?? ""); return true; },
                tooltip);

        private static ElementBounds AddCore(PageBuildContext ctx, ElementBounds bounds, string key, InputKind kind,
                                             Func<string> current, Func<string, bool> tryCommit, string tooltip)
        {
            var compo = ctx.Compo;
            var state = ctx.State;

            // SetValue fires TextChanged like a keystroke; guard against echoing our own fill.
            bool loading = false;

            compo.AddInteractiveElement(new PaneTextInput(compo.Api, bounds, text =>
            {
                if (loading) return;
                bool committed = tryCommit(text);
                // Keep a draft if text differs from the formatted value ("1.50"), so a rebuild doesn't reformat it.
                if (committed && text == current()) state.Drafts.Remove(key);
                else state.Drafts[key] = text;
            }, CairoFont.WhiteSmallText()), key);

            if (tooltip != null) Tooltip.Add(compo, tooltip, bounds, "tip-" + key);

            ctx.AfterCompose(() =>
            {
                var input = compo.GetTextInput(key);
                if (input == null) return;

                string shown = current();
                if (state.Drafts.TryGetValue(key, out string draft))
                {
                    // Drop the draft once it matches the stored value; otherwise keep showing it.
                    if (kind != InputKind.Text && NumberParse.IsAccepted(draft, kind) && ReadsAs(draft, shown, kind))
                        state.Drafts.Remove(key);
                    else
                        shown = draft;
                }

                loading = true;
                try { input.SetValue(shown); }
                finally { loading = false; }
            });

            return bounds;
        }

        private static bool ReadsAs(string draft, string shown, InputKind kind)
        {
            if (kind == InputKind.Integer)
                return NumberParse.TryInt(draft, out int a) && NumberParse.TryInt(shown, out int b) && a == b;
            return NumberParse.TryDouble(draft, out double x) && NumberParse.TryDouble(shown, out double y)
                   && Math.Abs(x - y) < 1e-9;
        }

        private static string RangeTooltip(string tooltip, double min, double max, double noMin, double noMax)
        {
            bool hasMin = min != noMin, hasMax = max != noMax;
            if (!hasMin && !hasMax) return tooltip;

            string range = hasMin && hasMax
                ? Lang.Get("canevents:admin-field-range", NumberParse.Format(min), NumberParse.Format(max))
                : hasMin
                    ? Lang.Get("canevents:admin-field-min", NumberParse.Format(min))
                    : Lang.Get("canevents:admin-field-max", NumberParse.Format(max));
            return string.IsNullOrEmpty(tooltip) ? range : tooltip + "\n" + range;
        }
    }
}
