using System;
using System.Collections.Generic;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace canevents.src.Gui.Admin.GuiElements
{
    // Base for admin list cells: cached texture, hover highlights, right-edge click zones, GL cleanup.
    public abstract class CANGuiElementCellBase : GuiElementTextBase, IGuiElementCell, IDisposable
    {
        protected enum HighlightZone
        {
            Left, Middle, Right
        }

        // Vanilla hit tests use this constant, so highlights must too.
        protected static double UnscaledRightBoxWidth => GuiElementMainMenuCell.unscaledRightBoxWidth;

        protected const double UnscaledSwitchPadding = 4.0;
        protected const double UnscaledSwitchSize = 25.0;

        protected readonly ICoreClientAPI capi;

        protected readonly List<GuiElement> children = new List<GuiElement>();

        // Separate from children: composed via Compose() and need BeforeCalcBounds() on measure.
        protected readonly List<GuiElementRichtext> richTexts = new List<GuiElementRichtext>();

        // Composer AddHoverText can't reach inside a cell. Not in children so they never take clicks.
        private readonly List<GuiElementHoverText> hoverTexts = new List<GuiElementHoverText>();

        public bool On;

        public Action<int> OnMouseDownOnCellLeft;
        public Action<int> OnMouseDownOnCellMiddle;
        public Action<int> OnMouseDownOnCellRight;

        private LoadedTexture cellTexture;
        private int leftHighlightTextureId;
        private int middleHighlightTextureId;
        private int rightHighlightTextureId;
        protected int switchOnTextureId;

        ElementBounds IGuiElementCell.Bounds => Bounds;

        protected CANGuiElementCellBase(ICoreClientAPI capi, ElementBounds bounds)
            : base(capi, "", null, bounds)
        {
            this.capi = capi;
            this.Font = CairoFont.WhiteSmallishText();
            cellTexture = new LoadedTexture(capi);
        }

        protected abstract void ComposeContent(Context ctx, ImageSurface surface);

        protected virtual double MinCellHeight => 73.0;

        // 1, 2 or 3.
        protected virtual int ClickZones => 3;

        // False skips building the three highlight textures and zone clicks.
        protected virtual bool UseHoverHighlights => true;

        protected virtual bool ShowModifyIcons => true;

        // Call from the constructor.
        protected void AddTooltip(ElementBounds bounds, string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            // Sized to the text, same as Widgets.Tooltip.
            CairoFont font = CairoFont.WhiteSmallText();
            double width = font.GetTextExtents(text).Width + 16;
            if (width > 320) width = 320;

            hoverTexts.Add(new GuiElementHoverText(capi, Widgets.Tooltip.EscapeVtml(text), font, (int)width, bounds.FlatCopy()));
        }

        protected void AddText(string text, ElementBounds bounds, double[] color, int fontSize = 15)
        {
            if (string.IsNullOrEmpty(text)) return;

            CairoFont font = CairoFont.WhiteMediumText().WithFontSize(fontSize).WithColor(color);
            // Richtextify parses VTML, and text like "<1m" would break it.
            string plain = Widgets.Tooltip.EscapeVtml(text);
            richTexts.Add(new GuiElementRichtext(capi, VtmlUtil.Richtextify(capi, plain, font), bounds));
        }

        // Composed once, then cached in cellTexture.
        private void Compose()
        {
            if (UseHoverHighlights)
            {
                ComposeHover(HighlightZone.Left, ref leftHighlightTextureId);
                ComposeHover(HighlightZone.Middle, ref middleHighlightTextureId);
                ComposeHover(HighlightZone.Right, ref rightHighlightTextureId);
            }
            GenOnTexture();

            ImageSurface surface = new ImageSurface(Format.Argb32, Bounds.OuterWidthInt, Bounds.OuterHeightInt);
            Context ctx = new Context(surface);
            Bounds.CalcWorldBounds();

            ComposeContent(ctx, surface);

            EmbossRoundRectangleElement(ctx, 0.0, 0.0, Bounds.OuterWidth, Bounds.OuterHeight, inverse: false, (int)GuiElement.scaled(4.0), 0);

            foreach (var child in children) child.ComposeElements(ctx, surface);
            foreach (var rich in richTexts)
            {
                rich.BeforeCalcBounds();
                rich.Compose();
            }

            generateTexture(surface, ref cellTexture);
            ctx.Dispose();
            surface.Dispose();

            ComposeHoverTexts();
        }

        // Scratch surface: hover texts use their own textures, anything drawn on the cell would stick.
        private void ComposeHoverTexts()
        {
            if (hoverTexts.Count == 0) return;

            ImageSurface scratch = new ImageSurface(Format.Argb32, 1, 1);
            Context scratchCtx = new Context(scratch);
            foreach (var hover in hoverTexts)
            {
                hover.Bounds.CalcWorldBounds();
                hover.ComposeElements(scratchCtx, scratch);
            }
            scratchCtx.Dispose();
            scratch.Dispose();
        }

        private void GenOnTexture()
        {
            double size = GuiElement.scaled(UnscaledSwitchSize - 2.0 * UnscaledSwitchPadding);
            ImageSurface surface = new ImageSurface(Format.Argb32, (int)size, (int)size);
            Context ctx = genContext(surface);
            GuiElement.RoundRectangle(ctx, 0.0, 0.0, size, size, 2.0);
            GuiElement.fillWithPattern(api, ctx, GuiElement.waterTextureName);
            generateTexture(surface, ref switchOnTextureId);
            ctx.Dispose();
            surface.Dispose();
        }

        private void ComposeHover(HighlightZone zone, ref int textureId)
        {
            ImageSurface surface = new ImageSurface(Format.Argb32, (int)Bounds.OuterWidth, (int)Bounds.OuterHeight);
            Context ctx = genContext(surface);
            double w = GuiElement.scaled(UnscaledRightBoxWidth);

            // Must match ZoneAt. With one zone the whole row lights up.
            double leftEdge = ClickZones >= 3 ? Bounds.InnerWidth - w * 2
                            : ClickZones == 2 ? Bounds.InnerWidth - w
                            : Bounds.OuterWidth;

            ctx.NewPath();
            switch (zone)
            {
                case HighlightZone.Left:
                    ctx.LineTo(0.0, 0.0);
                    ctx.LineTo(leftEdge, 0.0);
                    ctx.LineTo(leftEdge, Bounds.OuterHeight);
                    ctx.LineTo(0.0, Bounds.OuterHeight);
                    break;
                case HighlightZone.Middle:
                    ctx.LineTo(Bounds.InnerWidth - w * 2, 0.0);
                    ctx.LineTo(Bounds.InnerWidth - w, 0.0);
                    ctx.LineTo(Bounds.InnerWidth - w, Bounds.OuterHeight);
                    ctx.LineTo(Bounds.InnerWidth - w * 2, Bounds.OuterHeight);
                    break;
                default:
                    ctx.LineTo(Bounds.InnerWidth - w, 0.0);
                    ctx.LineTo(Bounds.OuterWidth, 0.0);
                    ctx.LineTo(Bounds.OuterWidth, Bounds.OuterHeight);
                    ctx.LineTo(Bounds.InnerWidth - w, Bounds.OuterHeight);
                    break;
            }
            ctx.ClosePath();

            ctx.SetSourceRGBA(0.0, 0.0, 0.0, 0.15);
            ctx.Fill();
            generateTexture(surface, ref textureId);
            ctx.Dispose();
            surface.Dispose();
        }

        public virtual void UpdateCellHeight()
        {
            Bounds.CalcWorldBounds();
            foreach (var rich in richTexts) rich.BeforeCalcBounds();
            if (ShowModifyIcons && Bounds.fixedHeight < MinCellHeight)
            {
                Bounds.fixedHeight = MinCellHeight;
            }
        }

        public override void RenderInteractiveElements(float deltaTime)
        {
            base.RenderInteractiveElements(deltaTime);
            foreach (var child in children) child.RenderInteractiveElements(deltaTime);
        }

        // Shared by highlight drawing and click handling.
        private HighlightZone ZoneAt(Vec2d posInside)
        {
            double w = GuiElement.scaled(UnscaledRightBoxWidth);

            if (ClickZones >= 2 && posInside.X > Bounds.InnerWidth - w) return HighlightZone.Right;
            if (ClickZones >= 3 && posInside.X > Bounds.InnerWidth - w * 2) return HighlightZone.Middle;
            return HighlightZone.Left;
        }

        public virtual void OnRenderInteractiveElements(ICoreClientAPI api, float deltaTime)
        {
            if (cellTexture.TextureId == 0)
            {
                Compose();
            }

            api.Render.Render2DTexturePremultipliedAlpha(cellTexture.TextureId, (int)Bounds.absX, (int)Bounds.absY, Bounds.OuterWidthInt, Bounds.OuterHeightInt);

            foreach (var child in children) child.RenderInteractiveElements(deltaTime);
            foreach (var rich in richTexts) rich.RenderInteractiveElements(deltaTime);

            RenderOverlay(api, deltaTime);

            foreach (var hover in hoverTexts) hover.RenderInteractiveElements(deltaTime);

            if (!UseHoverHighlights) return;

            Vec2d posInside = Bounds.PositionInside(api.Input.MouseX, api.Input.MouseY);
            if (posInside == null) return;

            int highlight;
            switch (ZoneAt(posInside))
            {
                case HighlightZone.Right: highlight = rightHighlightTextureId; break;
                case HighlightZone.Middle: highlight = middleHighlightTextureId; break;
                default: highlight = leftHighlightTextureId; break;
            }
            api.Render.Render2DTexturePremultipliedAlpha(highlight, (int)Bounds.absX, (int)Bounds.absY, Bounds.OuterWidth, Bounds.OuterHeight);
        }

        // Per-frame drawing over the cached texture (item renders, not Cairo).
        protected virtual void RenderOverlay(ICoreClientAPI api, float deltaTime) { }

        public virtual void OnMouseUpOnElement(MouseEvent args, int elementIndex)
        {
            Vec2d posInside = Bounds.PositionInside(api.Input.MouseX, api.Input.MouseY);
            if (posInside == null) return;

            int mouseX = api.Input.MouseX;
            int mouseY = api.Input.MouseY;

            // Children aren't hit-tested by the composer; a button click must not also fire the row action.
            bool onChild = false;
            foreach (var child in children)
            {
                if (!child.Bounds.PointInside(mouseX, mouseY)) continue;
                onChild = true;
                child.OnMouseUpOnElement(api, args);
            }

            if (onChild)
            {
                args.Handled = true;
                return;
            }

            if (!UseHoverHighlights) return;

            api.Gui.PlaySound("menubutton_press");

            switch (ZoneAt(posInside))
            {
                case HighlightZone.Right: OnMouseDownOnCellRight?.Invoke(elementIndex); break;
                case HighlightZone.Middle: OnMouseDownOnCellMiddle?.Invoke(elementIndex); break;
                default: OnMouseDownOnCellLeft?.Invoke(elementIndex); break;
            }
            args.Handled = true;
        }

        public virtual void OnMouseMoveOnElement(MouseEvent args, int elementIndex)
        {
            foreach (var child in children) child.OnMouseMove(api, args);
            foreach (var hover in hoverTexts) hover.OnMouseMove(api, args);
        }

        public virtual void OnMouseDownOnElement(MouseEvent args, int elementIndex)
        {
            foreach (var child in children)
            {
                if (child.Bounds.PointInside(args.X, args.Y)) child.OnMouseDownOnElement(api, args);
            }
        }

        public override void Dispose()
        {
            base.Dispose();
            foreach (var child in children) child.Dispose();
            foreach (var rich in richTexts) rich.Dispose();
            foreach (var hover in hoverTexts) hover.Dispose();
            cellTexture?.Dispose();
            // Zeroed: the engine disposes the composer again later, and a freed id may be reused by then.
            DeleteTexture(ref leftHighlightTextureId);
            DeleteTexture(ref middleHighlightTextureId);
            DeleteTexture(ref rightHighlightTextureId);
            DeleteTexture(ref switchOnTextureId);
        }

        private void DeleteTexture(ref int textureId)
        {
            if (textureId == 0) return;
            api.Render.GLDeleteTexture(textureId);
            textureId = 0;
        }
    }
}
