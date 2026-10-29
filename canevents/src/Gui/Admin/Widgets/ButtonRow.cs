using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace canevents.src.Gui.Admin.Widgets
{
    // Buttons sized to their captions, wrapping to a new line at maxWidth.
    public sealed class ButtonRow
    {
        // Borders take ~3 px of it on each side (see ButtonExtent).
        private const double Spacing = 14;

        // Bounds are 28 tall, but a Normal button draws its border outside them (34 total).
        public const double ButtonHeight = 28;
        public const double ButtonExtent = 34;

        public const double LineStep = ButtonExtent + 10;

        // +34 covers the button's own padding, less makes captions overlap.
        public static double ButtonWidth(string caption) =>
            CairoFont.ButtonText().GetTextExtents(caption ?? "").Width + 34;

        // Rough wrapped height, for framing a card before its content is added.
        public static double TextHeight(string text, double width, double lineHeight = 18)
        {
            double textWidth = CairoFont.WhiteSmallText().GetTextExtents(text ?? "").Width;
            int lines = (int)Math.Ceiling(textWidth / Math.Max(1, width));
            return Math.Max(1, lines) * lineHeight;
        }

        public static double MultiLineHeight(string text, double width)
        {
            double h = 0;
            foreach (var line in (text ?? "").Split('\n'))
                h += TextHeight(line, width);
            return h;
        }

        // Must match the wrapping rule in Next.
        public static double HeightFor(double maxWidth, double startX, params string[] captions)
            => HeightForWidths(maxWidth, startX, System.Linq.Enumerable.ToArray(
                System.Linq.Enumerable.Select(captions, ButtonWidth)));

        public static double HeightForWidths(double maxWidth, double startX, params double[] widths)
        {
            int lines = 1;
            double offsetX = startX;

            foreach (double width in widths)
            {
                if (offsetX > startX && offsetX + width > maxWidth)
                {
                    lines++;
                    offsetX = startX;
                }
                offsetX += width + Spacing;
            }

            return ButtonExtent + (lines - 1) * LineStep;
        }

        private readonly GuiComposer compo;
        private readonly double maxWidth;
        private readonly double startX;
        private readonly string keyPrefix;
        private ElementBounds row;
        private double offsetX;
        private int count;

        public ButtonRow(GuiComposer compo, ElementBounds row, double maxWidth, double startX = 0, string keyPrefix = null)
        {
            this.compo = compo;
            this.maxWidth = maxWidth;
            this.startX = startX;
            this.keyPrefix = keyPrefix ?? "btnrow-" + (int)row.fixedX + "-" + (int)row.fixedY;
            this.row = row.FlatCopy().WithAlignment(EnumDialogArea.LeftTop).WithFixedHeight(ButtonHeight);
            this.offsetX = startX;
        }

        // Current line; anchor for whatever goes below.
        public ElementBounds Bounds => row;

        // For non-button controls placed in the row.
        public ElementBounds Next(double width)
        {
            if (offsetX > startX && offsetX + width > maxWidth)
            {
                row = row.BelowCopy(0, LineStep - ButtonHeight);
                offsetX = startX;
            }

            var target = row.FlatCopy().WithAlignment(EnumDialogArea.LeftTop).WithFixedSize(width, ButtonHeight);
            target.fixedX += offsetX;
            offsetX += width + Spacing;
            return target;
        }

        public ElementBounds Add(string caption, Func<bool> onClick, string tooltip = null)
        {
            var target = Next(ButtonWidth(caption));
            string key = keyPrefix + "-" + (count++);

            compo.AddButton(caption, new ActionConsumable(onClick), target, EnumButtonStyle.Normal, key);
            if (tooltip != null) Tooltip.Add(compo, tooltip, target, "tip-" + key);
            return target;
        }

        public ElementBounds Add(string caption, Action onClick, string tooltip = null)
            => Add(caption, () => { onClick(); return true; }, tooltip);
    }
}
