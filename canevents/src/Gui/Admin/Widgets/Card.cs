using System.Collections.Generic;
using Vintagestory.API.Client;

namespace canevents.src.Gui.Admin.Widgets
{
    public sealed class CardRow
    {
        public string Label;
        public string Value;

        // null = AdminColors.Value.
        public double[] ValueColor;

        public string Tooltip;

        // Element key suffix, unique within the page.
        public string Key;
    }

    public static class Card
    {
        public const double LineHeight = 17;

        private const double HeaderTextHeight = 20;
        private const double HeaderRuleGap = 5;
        private const double RuleBottomGap = 6;

        // +2 is the rule itself.
        public const double HeaderHeight = HeaderTextHeight + HeaderRuleGap + 2 + RuleBottomGap;

        public const double Padding = 8;
        public const double Gap = 12;
        public const double ColumnGap = 18;

        public const double ValueWidth = 66;

        public const double ActionSize = 30;
        public const double ActionGap = 7;

        public static double HeightFor(int rowCount) => HeaderHeight + rowCount * LineHeight + Padding * 2;

        public static double HeightAround(double contentHeight, bool withTitle = true)
            => (withTitle ? HeaderHeight : 0) + contentHeight + Padding * 2;

        // Returns the inner bounds below the heading.
        public static ElementBounds Frame(GuiComposer compo, ElementBounds column, double y, double height, string title)
        {
            var card = column.FlatCopy();
            card.fixedY = y;
            card.fixedHeight = height;
            compo.AddInset(card, 2);

            double innerWidth = column.fixedWidth - Padding * 2;

            var inner = column.FlatCopy().WithFixedSize(innerWidth, height - Padding * 2);
            inner.fixedX += Padding;
            inner.fixedY = y + Padding;

            if (title != null)
            {
                var header = inner.FlatCopy().WithFixedHeight(HeaderTextHeight);
                compo.AddStaticText(title, CairoFont.WhiteSmallishText().WithColor(AdminColors.Section),
                    header, HeaderKey(compo, title));

                var rule = header.BelowCopy(0, HeaderRuleGap).WithFixedHeight(2);
                compo.AddInset(rule);

                inner.fixedY += HeaderHeight;
                inner.fixedHeight -= HeaderHeight;
            }

            return inner;
        }

        // Small Refresh button in the card heading's top-right corner.
        public static void AddRefresh(Pages.PageBuildContext ctx, ElementBounds column, double cardY, string key,
                                      System.Action onClick)
        {
            string caption = Vintagestory.API.Config.Lang.Get("canevents:admin-debug-refresh");
            double w = ButtonRow.ButtonWidth(caption) - 10;
            var b = ElementBounds.Fixed(column.fixedX + column.fixedWidth - Padding - w, cardY + Padding, w, 20);
            ctx.Compo.AddSmallButton(caption, new Vintagestory.API.Common.ActionConsumable(() => { onClick(); return true; }),
                b, EnumButtonStyle.Small, key);
        }

        // Lang keys: langPrefix + "unsaved" / "saved".
        public static void AddDirtyLine(Pages.PageBuildContext ctx, double x, double y, double width, string key,
                                        bool dirty, string langPrefix)
        {
            var font = CairoFont.WhiteSmallText().WithColor(dirty ? AdminColors.Warning : AdminColors.Muted);
            string text = Vintagestory.API.Config.Lang.Get(langPrefix + (dirty ? "unsaved" : "saved"));
            ctx.Compo.AddStaticText(Fit(text, font, width), font, ElementBounds.Fixed(x, y, width, 20), key);
        }

        // Composer throws on duplicate keys, and two cards can share a heading.
        internal static string HeaderKey(GuiComposer compo, string title)
        {
            string key = "sec-" + title;
            for (int i = 2; compo.GetElement(key) != null; i++)
            {
                key = "sec-" + title + "-" + i;
            }
            return key;
        }

        // Returns y of the next card.
        public static double Rows(GuiComposer compo, ElementBounds column, double y, string title, List<CardRow> rows)
            => Rows(compo, column, y, title, rows, 1);

        public static double Rows(GuiComposer compo, ElementBounds column, double y, string title,
                                  List<CardRow> rows, int subColumns)
        {
            int perColumn = PerColumn(rows.Count, subColumns);
            double height = HeightFor(perColumn);
            ElementBounds inner = Frame(compo, column, y, height, title);

            DrawRows(compo, inner, rows, subColumns);

            return y + height + Gap;
        }

        // actions gets the first button slot.
        public static double RowsWithActions(GuiComposer compo, ElementBounds column, double y, string title,
                                             List<CardRow> rows, System.Action<ElementBounds> actions,
                                             double actionSize = ActionSize)
        {
            double height = HeightFor(rows.Count) + ActionGap + actionSize;
            ElementBounds inner = Frame(compo, column, y, height, title);

            DrawRows(compo, inner, rows, 1);

            var slot = inner.FlatCopy().WithFixedSize(actionSize, actionSize);
            slot.fixedY = inner.fixedY + rows.Count * LineHeight + ActionGap;
            actions(slot);

            return y + height + Gap;
        }

        public static double Actions(GuiComposer compo, ElementBounds column, double y, string title,
                                     System.Action<ElementBounds> actions, double actionSize = ActionSize)
            => RowsWithActions(compo, column, y, title, new List<CardRow>(), actions, actionSize);

        public static ElementBounds NextAction(ElementBounds slot) => slot.RightCopy(ActionGap);

        private static int PerColumn(int rowCount, int subColumns)
        {
            if (subColumns < 1) subColumns = 1;
            return (rowCount + subColumns - 1) / subColumns;
        }

        private static void DrawRows(GuiComposer compo, ElementBounds inner, List<CardRow> rows, int subColumns)
        {
            if (subColumns < 1) subColumns = 1;
            int perColumn = PerColumn(rows.Count, subColumns);

            const double subGap = 10;
            double subWidth = (inner.fixedWidth - subGap * (subColumns - 1)) / subColumns;
            double valueWidth = subColumns == 1 ? ValueWidth : subWidth * 0.4;

            for (int i = 0; i < rows.Count; i++)
            {
                CardRow row = rows[i];
                double subX = (i / perColumn) * (subWidth + subGap);
                double rowY = inner.fixedY + (i % perColumn) * LineHeight;

                var labelFont = CairoFont.WhiteSmallText().WithColor(AdminColors.Label);
                var valueFont = CairoFont.WhiteSmallText().WithColor(row.ValueColor ?? AdminColors.Value)
                                         .WithOrientation(EnumTextOrientation.Right);

                // Long values take width from the label (up to 70%) instead of wrapping.
                double rowValueWidth = System.Math.Max(valueWidth,
                    System.Math.Min(valueFont.GetTextExtents(row.Value ?? "").Width + 6, subWidth * 0.7));
                double labelWidth = subWidth - rowValueWidth - 4;

                var labelBounds = inner.FlatCopy().WithFixedSize(labelWidth, LineHeight);
                labelBounds.fixedX += subX;
                labelBounds.fixedY = rowY;

                // Static text wraps into the next row, so truncate and show the full label on hover.
                string shown = Fit(row.Label, labelFont, labelWidth);
                compo.AddStaticText(shown, labelFont, labelBounds, "cardlabel-" + row.Key);
                if (shown != row.Label && row.Tooltip == null)
                {
                    Tooltip.Add(compo, row.Label, labelBounds, "cardfull-" + row.Key);
                }

                var valueBounds = inner.FlatCopy().WithFixedSize(rowValueWidth, LineHeight);
                valueBounds.fixedX += subX + subWidth - rowValueWidth;
                valueBounds.fixedY = rowY;
                compo.AddStaticText(Fit(row.Value, valueFont, rowValueWidth), valueFont,
                    valueBounds, "cardvalue-" + row.Key);

                if (row.Tooltip != null) Tooltip.Add(compo, row.Tooltip, labelBounds, "cardtip-" + row.Key);
            }
        }

        public static string Fit(string text, CairoFont font, double maxWidth)
        {
            if (string.IsNullOrEmpty(text) || font.GetTextExtents(text).Width <= maxWidth) return text;

            string shortened = text;
            while (shortened.Length > 1 && font.GetTextExtents(shortened + "...").Width > maxWidth)
            {
                shortened = shortened.Substring(0, shortened.Length - 1);
            }
            return shortened + "...";
        }

        // Truncated single line with the full text on hover. Returns the shown text.
        public static string AddFitLine(GuiComposer compo, string text, CairoFont font, ElementBounds bounds,
            string key, string tipKey)
        {
            string shown = Fit(text, font, bounds.fixedWidth);
            compo.AddStaticText(shown ?? "", font, bounds, key);
            if (shown != text) Tooltip.Add(compo, text, bounds, tipKey);
            return shown;
        }
    }
}
