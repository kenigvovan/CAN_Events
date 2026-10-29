using canevents.src.Gui.Admin.Pages;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace canevents.src.Gui.Admin.Widgets
{
    // Collapsible card. Open state is kept in AdminGuiState.Sections so it survives rebuilds.
    public static class Section
    {
        private const double ToggleWidth = 26;
        private const double ToggleHeight = 20;
        private const double CollapsedTextHeight = 20;

        public const double CollapsedHeight = CollapsedTextHeight + Card.Padding * 2;

        // Returns inner bounds to fill, or null when collapsed. contentHeight excludes the heading.
        public static ElementBounds Begin(PageBuildContext ctx, ElementBounds column, double y, string key,
                                          string title, double contentHeight, out double nextY,
                                          bool defaultOpen = true, string tooltip = null)
        {
            var compo = ctx.Compo;
            bool open = ctx.State.IsSectionOpen(key, defaultOpen);

            ElementBounds inner;
            double height;

            if (open)
            {
                height = Card.HeightAround(contentHeight);
                inner = Card.Frame(compo, column, y, height, title);
            }
            else
            {
                height = CollapsedHeight;
                var card = column.FlatCopy();
                card.fixedY = y;
                card.fixedHeight = height;
                compo.AddInset(card, 2);

                var header = column.FlatCopy().WithFixedSize(column.fixedWidth - Card.Padding * 2 - ToggleWidth, CollapsedTextHeight);
                header.fixedX += Card.Padding;
                header.fixedY = y + Card.Padding;
                compo.AddStaticText(title, CairoFont.WhiteSmallishText().WithColor(AdminColors.Muted),
                    header, Card.HeaderKey(compo, title));
                inner = null;
            }

            // Top-right corner of the card, level with the heading text.
            var toggle = column.FlatCopy().WithFixedSize(ToggleWidth, ToggleHeight);
            toggle.fixedX += column.fixedWidth - Card.Padding - ToggleWidth;
            toggle.fixedY = y + Card.Padding;

            var state = ctx.State;
            var gui = ctx.Gui;
            compo.AddSmallButton(open ? "-" : "+", new ActionConsumable(() =>
            {
                state.Sections[key] = !open;
                gui.RequestRebuild();
                return true;
            }), toggle, EnumButtonStyle.Small, "sectoggle-" + key);
            Tooltip.Add(compo, tooltip ?? Lang.Get(open ? "canevents:admin-section-collapse" : "canevents:admin-section-expand"),
                toggle, "sectoggle-tip-" + key);

            nextY = y + height + Card.Gap;
            return inner;
        }
    }
}
