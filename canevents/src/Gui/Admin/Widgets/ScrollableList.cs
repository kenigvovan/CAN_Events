using System;
using System.Collections.Generic;
using canevents.src.Gui.Admin.Pages;
using Vintagestory.API.Client;

namespace canevents.src.Gui.Admin.Widgets
{
    public sealed class ScrollableListOptions
    {
        // Clip height = main height minus this.
        public double HeightReserve = 300;

        // Also used for the scrollbar key (+"-scrollbar") and the remembered scroll offset.
        public string Key = "listcells";

        // null = centered white medium text.
        public CairoFont TitleFont;

        public bool ContainerBelowTitle;

        public double TitleHeightShrink = 50;

        public double TitleGap = 0;

        // Parent for the clipped list; null = the title row.
        public ElementBounds Container;

        // Tighter than vanilla defaults.
        public int CellSpacing = 4;
        public int CellVerPadding = 2;
        public int CellHorPadding = 4;
    }

    public sealed class ScrollableListLayout
    {
        public ElementBounds Title;
        public ElementBounds Inset;
        public ElementBounds Clip;
        public ElementBounds List;
        public string ListKey;
        public string ScrollbarKey;

        // Call after Compose. Restore only after SetHeights, which fires the callback with 0.
        public void ApplyScrollbarHeights(GuiComposer compo, float restoreTo = 0f)
        {
            var scrollbar = compo.GetScrollbar(ScrollbarKey);
            if (scrollbar == null) return;

            float visible = (float)Clip.fixedHeight;
            float total = (float)List.fixedHeight;
            scrollbar.SetHeights(visible, total);

            float max = Math.Max(0f, total - visible);
            float wanted = Math.Clamp(restoreTo, 0f, max);
            if (wanted <= 0f) return;

            scrollbar.CurrentYPosition = wanted;
            scrollbar.TriggerChanged();
        }
    }

    public static class ScrollableList
    {
        private const double TitleRowY = 40;
        private const double TitleRowHeight = 30;

        private const double ListGap = 5;
        private const double InsetGrow = 6;

        private static double TitleRowOffset(bool hasTitle)
            => hasTitle ? TitleRowY + TitleRowHeight + ListGap : 0;

        // HeightReserve that makes the clip exactly listHeight tall.
        public static double ReserveFor(ElementBounds main, double listHeight)
            => main.fixedHeight - listHeight;

        // Scroll offset is kept in AdminGuiState.Scroll and restored after compose.
        public static ScrollableListLayout Add<TCell>(
            PageBuildContext ctx,
            ElementBounds anchor,
            string title,
            IEnumerable<TCell> items,
            OnRequireCell<TCell> cellFactory,
            ScrollableListOptions opts = null)
        {
            opts = opts ?? new ScrollableListOptions();
            var state = ctx.State;
            string key = opts.Key;

            float restoreTo = state.ScrollOf(key);
            var layout = Add(ctx.Compo, ctx.Main, anchor, title, items, cellFactory, opts,
                value => state.Scroll[key] = value);

            var compo = ctx.Compo;
            ctx.AfterCompose(() => layout.ApplyScrollbarHeights(compo, restoreTo));
            return layout;
        }

        // Caller must run ApplyScrollbarHeights after Compose.
        public static ScrollableListLayout Add<TCell>(
            GuiComposer compo,
            ElementBounds main,
            ElementBounds anchor,
            string title,
            IEnumerable<TCell> items,
            OnRequireCell<TCell> cellFactory,
            ScrollableListOptions opts = null,
            Action<float> onScrolled = null)
        {
            opts = opts ?? new ScrollableListOptions();

            ElementBounds outer = anchor.FlatCopy();
            double width = outer.fixedWidth - 30;

            ElementBounds listArea = ElementBounds
                .Fixed(0, TitleRowOffset(title != null), width, Math.Max(40, main.fixedHeight - opts.HeightReserve));

            ElementBounds titleBounds = outer.BelowCopy(0, opts.TitleGap);
            // Zero-height anchors would go negative.
            titleBounds.fixedHeight = Math.Max(0, titleBounds.fixedHeight - opts.TitleHeightShrink);

            ElementBounds clippingBounds = listArea.ForkBoundingParent();
            ElementBounds insetBounds = listArea.FlatCopy().FixedGrow(InsetGrow).WithFixedOffset(-InsetGrow / 2, -InsetGrow / 2);
            ElementBounds scrollbarBounds = insetBounds.CopyOffsetedSibling(listArea.fixedWidth + 7).WithFixedWidth(20);

            string listKey = opts.Key;
            string scrollbarKey = listKey + "-scrollbar";

            if (title != null)
            {
                compo.AddStaticText(title,
                    opts.TitleFont ?? CairoFont.WhiteMediumText().WithOrientation(EnumTextOrientation.Center),
                    titleBounds, listKey + "-title");
            }

            ElementBounds clipInner = insetBounds.ForkContainingChild(3.0, 3.0, 3.0, 3.0);
            ElementBounds listBounds = clipInner.ForkContainingChild(0.0, 0.0, 0.0, -3.0).WithFixedPadding(5.0);

            ElementBounds container = opts.Container
                ?? (opts.ContainerBelowTitle ? titleBounds.BelowCopy() : titleBounds);

            compo.BeginChildElements(container)
                .BeginClip(clippingBounds)
                // Keeps the list inside an outer ScrollPane clip.
                .AddInteractiveElement(new NestedClipScissor(compo.Api, clippingBounds))
                .AddInset(insetBounds, 3)
                .AddCellList(listBounds, cellFactory, items, listKey)
                .EndClip()
                // Otherwise the first scrollbar added (e.g. ScrollPane's) takes the wheel.
                .AddInteractiveElement(new ListWheelForwarder(compo, scrollbarKey,
                    ElementBounds.Fixed(insetBounds.fixedX, insetBounds.fixedY, insetBounds.fixedWidth, insetBounds.fixedHeight)),
                    listKey + "-wheel")
                .AddVerticalScrollbar((float value) =>
                {
                    onScrolled?.Invoke(value);
                    ElementBounds bounds = compo.GetCellList<TCell>(listKey).Bounds;
                    bounds.fixedY = 0f - value;
                    bounds.CalcWorldBounds();
                }, scrollbarBounds, scrollbarKey)
                .EndChildElements();

            var list = compo.GetCellList<TCell>(listKey);
            list.unscaledCellSpacing = opts.CellSpacing;
            list.UnscaledCellVerPadding = opts.CellVerPadding;
            list.UnscaledCellHorPadding = opts.CellHorPadding;
            list.BeforeCalcBounds();

            return new ScrollableListLayout
            {
                Title = titleBounds,
                Inset = insetBounds,
                Clip = clipInner,
                List = listBounds,
                ListKey = listKey,
                ScrollbarKey = scrollbarKey
            };
        }

        // The composer gives the wheel to the first scrollbar added; forward it to this list's bar.
        internal sealed class ListWheelForwarder : GuiElement
        {
            private readonly GuiComposer compo;
            private readonly string scrollbarKey;

            public ListWheelForwarder(GuiComposer compo, string scrollbarKey, ElementBounds bounds)
                : base(compo.Api, bounds)
            {
                this.compo = compo;
                this.scrollbarKey = scrollbarKey;
            }

            public override void OnMouseWheel(ICoreClientAPI api, MouseWheelEventArgs args)
            {
                if (args.IsHandled || !IsPositionInside(api.Input.MouseX, api.Input.MouseY)) return;
                compo.GetScrollbar(scrollbarKey)?.OnMouseWheel(api, args);
            }
        }
    }
}
