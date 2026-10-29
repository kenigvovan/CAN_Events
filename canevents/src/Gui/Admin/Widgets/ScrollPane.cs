using System;
using canevents.src.Gui.Admin.Pages;
using Vintagestory.API.Client;

namespace canevents.src.Gui.Admin.Widgets
{
    // Own composer: static elements are baked into the bg texture and can't scroll. Not owned by the panel, call Remove.
    public sealed class ScrollPane
    {
        public const double ScrollbarWidth = 20;
        private const double ScrollbarGap = 8;
        private const string ComposerPrefix = "canevents-admin-pane-";

        // Coordinates start at (0, 0) at the top of the scrolled content.
        public PageBuildContext Ctx { get; private set; }

        public double Width => clip.fixedWidth;

        private AdminPanelGui gui;
        private AdminGuiState state;
        private GuiComposer compo;
        private string key;
        private string composerName;
        private ElementBounds clip;
        private ElementBounds content;
        private double viewportHeight;
        private double contentHeight;
        private float offset;
        private bool suppressScroll;
        private bool recomposeQueued;

        // False if the page threw between Begin and End; the half-built composer is not composed.
        private bool ended;

        private ScrollPane() { }

        private static string NameOf(string key) => ComposerPrefix + key;

        // viewport is in page coordinates. Build through Ctx until End.
        public static ScrollPane Begin(PageBuildContext outer, ElementBounds viewport, string key)
        {
            var gui = outer.Gui;
            var pane = new ScrollPane
            {
                gui = gui,
                state = outer.State,
                key = key,
                composerName = NameOf(key),
                viewportHeight = viewport.fixedHeight
            };

            // Anchor in the panel composer; the pane composer is parented to it and follows the panel.
            var anchor = ElementBounds.Fixed(viewport.fixedX, viewport.fixedY, viewport.fixedWidth, viewport.fixedHeight);
            outer.Compo.AddStaticCustomDraw(anchor, (c, s, b) => { });

            double h = viewport.fixedHeight;
            double clipWidth = viewport.fixedWidth - ScrollbarWidth - ScrollbarGap;
            var paneBounds = ElementBounds.Fixed(0, 0, viewport.fixedWidth, h).WithParent(anchor);

            // The engine's composer manager disposes the previous composer of this name.
            var compo = gui.Capi.Gui.CreateCompo(pane.composerName, paneBounds);
            pane.compo = compo;

            pane.offset = outer.State.ScrollOf(key);
            pane.clip = ElementBounds.Fixed(0, 0, clipWidth, h);
            pane.content = ElementBounds.Fixed(0, -pane.offset, clipWidth, h);

            // Must be first to see clicks before hidden content does.
            compo.AddInteractiveElement(new PaneClickGuard(compo.Api, pane), key + "-guard");
            // Before the content, otherwise the wheel goes to the first inner list with a scrollbar.
            compo.AddVerticalScrollbar(pane.OnScrolled,
                ElementBounds.Fixed(clipWidth + ScrollbarGap, 0, ScrollbarWidth, h), key + "-scrollbar");

            compo.BeginClip(pane.clip);
            compo.BeginChildElements(pane.content);

            pane.Ctx = new PageBuildContext
            {
                Gui = gui,
                State = outer.State,
                Compo = compo,
                Main = ElementBounds.Fixed(0, 0, clipWidth, h),
                Current = ElementBounds.Fixed(0, 0, clipWidth, 0),
                Line = ElementBounds.Fixed(0, 0, clipWidth, 0)
            };

            // Anchor position is only known after the panel composes.
            outer.AfterCompose(pane.Compose);
            return pane;
        }

        // height = the y the content reached.
        public void End(double height)
        {
            compo.EndChildElements();
            compo.EndClip();

            contentHeight = Math.Max(0, height);
            float max = (float)Math.Max(0, contentHeight - viewportHeight);
            offset = Math.Clamp(offset, 0f, max);
            content.fixedY = -offset;
            state.Scroll[key] = offset;
            ended = true;
        }

        private void Compose()
        {
            // Never registered with the panel; the engine disposes it with the next pane of this name.
            if (!ended) return;

            compo.Compose(false);
            Ctx.RunAfterCompose();

            var scrollbar = compo.GetScrollbar(key + "-scrollbar");
            if (scrollbar != null)
            {
                // SetHeights fires the callback with 0; ignore it, content is already at the offset.
                suppressScroll = true;
                try
                {
                    scrollbar.SetHeights((float)viewportHeight, (float)Math.Max(contentHeight, viewportHeight));
                    scrollbar.CurrentYPosition = offset;
                }
                finally { suppressScroll = false; }
            }

            gui.Composers[composerName] = compo;
        }

        private void OnScrolled(float value)
        {
            if (suppressScroll || Math.Abs(value - offset) < 0.01f) return;
            offset = value;
            state.Scroll[key] = value;

            // Drag fires per mouse move. Recompose once per frame, outside the composer's event loop.
            if (recomposeQueued) return;
            recomposeQueued = true;
            gui.Capi.Event.EnqueueMainThreadTask(() =>
            {
                recomposeQueued = false;
                if (gui.Composers[composerName] != compo) return; // rebuilt or dropped meanwhile
                content.fixedY = -offset;
                content.MarkDirtyRecursive();
                content.CalcWorldBounds();
                compo.ReCompose();
            }, "canevents-admin-pane-scroll");
        }

        // Engine EndClip clears the scissor instead of restoring it, so hidden content still gets clicks.
        private bool SwallowsClickAt(int x, int y)
        {
            if (gui.Composers[composerName] != compo) return false;
            if (clip.PointInside(x, y)) return false;
            if (x < clip.absX || x > clip.absX + clip.OuterWidth) return false;
            double top = content.absY;
            double bottom = top + GuiElement.scaled(contentHeight);
            return y >= top && y <= bottom;
        }

        public static void Remove(AdminPanelGui gui, string key)
        {
            string name = NameOf(key);
            var compo = gui.Composers[name];
            if (compo == null) return;
            // Remove alone keeps its textures until the next pane of this name is created.
            compo.Dispose();
            gui.Composers.Remove(name);
        }

        // Clicks on the panel composer don't reach the pane, so a focused field here would block rebuilds.
        public static void Unfocus(AdminPanelGui gui, string key)
        {
            gui.Composers[NameOf(key)]?.UnfocusOwnElements();
        }

        // Eats clicks on hidden content.
        private sealed class PaneClickGuard : GuiElement
        {
            private readonly ScrollPane pane;

            public PaneClickGuard(ICoreClientAPI capi, ScrollPane pane)
                : base(capi, ElementBounds.Fixed(0, 0, 0, 0))
            {
                this.pane = pane;
            }

            public override void OnMouseDown(ICoreClientAPI api, MouseEvent args)
            {
                if (!args.Handled && pane.SwallowsClickAt(args.X, args.Y)) args.Handled = true;
            }
        }
    }

    // Engine text inputs scissor their text to their own box, ignoring the pane, then switch the scissor off,
    // so their digits and everything drawn after them escape the pane. Drawn only when wholly inside the
    // current clip; pushing null and popping it afterwards re-applies that clip.
    internal sealed class PaneTextInput : GuiElementTextInput
    {
        public PaneTextInput(ICoreClientAPI capi, ElementBounds bounds, Action<string> onTextChanged, CairoFont font)
            : base(capi, bounds, onTextChanged, font) { }

        public override void RenderInteractiveElements(float deltaTime)
        {
            var stack = api.Render.ScissorStack;
            var clip = stack.Count > 0 ? stack.Peek() : null;
            if (clip == null)
            {
                base.RenderInteractiveElements(deltaTime);
                return;
            }

            if (Bounds.renderY >= clip.renderY && Bounds.renderY + Bounds.InnerHeight <= clip.renderY + clip.InnerHeight)
                base.RenderInteractiveElements(deltaTime);
            api.Render.PushScissor(null);
            api.Render.PopScissor();
        }
    }

    // Engine BeginClip replaces the scissor; add right after it to intersect with the outer one.
    // The intersection itself goes on the stack: a stacking push keeps the raw bounds there, so any
    // later PopScissor inside the list (item icons do one) would widen the clip back past the pane.
    internal sealed class NestedClipScissor : GuiElement
    {
        private readonly ElementBounds clipBounds;
        private readonly ScreenRect rect = new ScreenRect();

        public NestedClipScissor(ICoreClientAPI capi, ElementBounds clipBounds)
            : base(capi, ElementBounds.Fixed(0, 0, 0, 0))
        {
            this.clipBounds = clipBounds;
        }

        public override void RenderInteractiveElements(float deltaTime)
        {
            api.Render.PopScissor();
            var stack = api.Render.ScissorStack;
            rect.SetIntersection(clipBounds, stack.Count > 0 ? stack.Peek() : null);
            api.Render.PushScissor(rect);
        }

        // Fixed screen rectangle; the scissor code only reads these four.
        private sealed class ScreenRect : ElementBounds
        {
            private double x, y, w, h;

            public override double renderX => x;
            public override double renderY => y;
            public override double InnerWidth => w;
            public override double InnerHeight => h;

            public void SetIntersection(ElementBounds a, ElementBounds b)
            {
                x = a.renderX; y = a.renderY;
                double x2 = x + a.InnerWidth, y2 = y + a.InnerHeight;
                if (b != null)
                {
                    x = Math.Max(x, b.renderX);
                    y = Math.Max(y, b.renderY);
                    x2 = Math.Min(x2, b.renderX + b.InnerWidth);
                    y2 = Math.Min(y2, b.renderY + b.InnerHeight);
                }
                w = Math.Max(0, x2 - x);
                h = Math.Max(0, y2 - y);
            }
        }
    }
}
