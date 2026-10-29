using Vintagestory.API.Client;

namespace canevents.src.Gui.Admin.Widgets
{
    // Always pass a unique key: unkeyed hover texts share a generated name and only the last survives.
    public static class Tooltip
    {
        private const double Padding = 16;
        private const double MaxWidth = 320;
        private const double MinHoverHeight = 25;

        // Don't raise to MinHoverHeight: card rows are 17 tall and their hover areas would overlap.
        private const double MinUsableHeight = 10;

        // A bare '<' or '>' is parsed as VTML and spams the client log ("Found closing tag char >").
        public static string EscapeVtml(string text)
            => string.IsNullOrEmpty(text) ? text : text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

        public static void Add(GuiComposer compo, string text, ElementBounds bounds, string key)
        {
            if (string.IsNullOrEmpty(text)) return;

            CairoFont font = CairoFont.WhiteSmallText().WithOrientation(EnumTextOrientation.Center);
            double width = font.GetTextExtents(text).Width + Padding;
            if (width > MaxWidth) width = MaxWidth;

            // Rows shrunk to their text (list headings) can have zero height.
            ElementBounds area = bounds;
            if (area.fixedHeight < MinUsableHeight)
            {
                area = bounds.FlatCopy();
                area.fixedHeight = MinHoverHeight;
            }

            compo.AddHoverText(EscapeVtml(text), font, (int)width, area, key);
        }
    }
}
