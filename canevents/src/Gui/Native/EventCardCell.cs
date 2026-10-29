using System;
using System.Collections.Generic;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace canevents.src.Gui.Native
{
    // Vanilla GuiElementCellList only stacks vertically, so two-column rows are one cell each.
    public class EventTileRow : GuiElementTextBase, IGuiElementCell
    {
        public const double UnscaledPadding = 10;
        public const double UnscaledStripeWidth = 4;
        public const double UnscaledTileGap = 10;
        public const double UnscaledButtonHeight = 26;
        // Ratio matches vanilla slots (48 / 25.6); item size scales the model, not a box, so long meshes need a scissor.
        public const double UnscaledIconSize = 30;
        public const double UnscaledIconItemSize = 17;
        public const double UnscaledIconRowHeight = 34;

        private readonly EventCard[] tiles;
        private readonly bool fullWidth;
        private readonly List<DummySlot>[] iconSlots;
        private readonly LoadedTexture[] normalTextures;
        private readonly LoadedTexture[] hoverTextures;
        private readonly double[] iconRowY;      // unscaled, relative to the tile's top

        // ItemstackComponentBase lifts the scissor, so the list's clip doesn't cut the tooltip.
        private ItemstackComponentBase tooltipRenderer;
        private ItemSlot hoveredIconSlot;

        public EventTileRow(ICoreClientAPI capi, EventCardRow row, ElementBounds bounds)
            : base(capi, "", CairoFont.WhiteSmallishText(), bounds)
        {
            tiles = row.Tiles;
            fullWidth = row.FullWidth;
            iconSlots = new List<DummySlot>[tiles.Length];
            normalTextures = new LoadedTexture[tiles.Length];
            hoverTextures = new LoadedTexture[tiles.Length];
            iconRowY = new double[tiles.Length];

            for (int i = 0; i < tiles.Length; i++)
            {
                normalTextures[i] = new LoadedTexture(capi);
                hoverTextures[i] = new LoadedTexture(capi);
                iconSlots[i] = new List<DummySlot>();
                if (tiles[i].Icons != null)
                    foreach (var stack in tiles[i].Icons) iconSlots[i].Add(new DummySlot(stack));
            }
        }

        ElementBounds IGuiElementCell.Bounds => Bounds;
        public ElementBounds InsideClipBounds { get; set; }

        public string MouseOverCursor
        {
            get
            {
                int i = TileAt(api.Input.MouseX, api.Input.MouseY);
                return i >= 0 && tiles[i].OnClick != null ? "linkselect" : null;
            }
        }

        // Scaled pixels.
        private double TileWidth => IsHeaderRow || fullWidth
            ? Bounds.InnerWidth
            : (Bounds.InnerWidth - scaled(UnscaledTileGap)) / 2;

        private bool IsHeaderRow => tiles[0].IsHeader;

        public void UpdateCellHeight()
        {
            Bounds.CalcWorldBounds();

            double height = 0;
            for (int i = 0; i < tiles.Length; i++) height = Math.Max(height, MeasureTile(i));

            // Both tiles share the taller height, hence bottom-anchored buttons.
            Bounds.fixedHeight = Math.Max(IsHeaderRow ? 26 : 78, height);
        }

        // Unscaled height the tile needs; also records where its icon row goes.
        private double MeasureTile(int i)
        {
            var card = tiles[i];
            if (card.IsHeader) return 26;

            double textWidth = TileWidth - scaled(UnscaledPadding) - scaled(UnscaledStripeWidth + 6);

            Font = TitleFontFor(card);
            double titleHeight = textUtil.GetMultilineTextHeight(Font, card.Title, textWidth) / RuntimeEnv.GUIScale;

            Font = DetailFont;
            double detailHeight = string.IsNullOrEmpty(card.Detail)
                ? 0
                : textUtil.GetMultilineTextHeight(Font, card.Detail, textWidth) / RuntimeEnv.GUIScale;

            double height = UnscaledPadding + titleHeight;
            if (detailHeight > 0) height += 4 + detailHeight;

            iconRowY[i] = height + 2;
            if (iconSlots[i].Count > 0) height += IconRows(i) * UnscaledIconRowHeight;

            if (card.Progress.HasValue) height += 8;      // fill bar plus its gap
            if (!string.IsNullOrEmpty(card.BadgeText)) height += UnscaledButtonHeight + 4;

            return height + UnscaledPadding;
        }

        public void Compose()
        {
            Bounds.CalcWorldBounds();

            int w = (int)TileWidth;
            int h = Bounds.OuterHeightInt;

            // Cairo is native: a degenerate surface size kills the process with no log.
            // Logged once, since OnRender retries Compose every frame while TextureId == 0.
            if (w <= 0 || h <= 0 || w > 8192 || h > 8192)
            {
                if (!skipReported)
                {
                    skipReported = true;
                    ClientTrace.Mark(() => "EventCardCell.Compose skipped, size " + w + "x" + h);
                }
                return;
            }

            skipReported = false;

            for (int i = 0; i < tiles.Length; i++)
            {
                var surface = new ImageSurface(Format.Argb32, w, h);
                var ctx = new Context(surface);

                Draw(ctx, tiles[i], i, w, h, hovered: false);
                generateTexture(surface, ref normalTextures[i]);

                ctx.Operator = Operator.Clear;
                ctx.Paint();
                ctx.Operator = Operator.Over;
                Draw(ctx, tiles[i], i, w, h, hovered: true);
                generateTexture(surface, ref hoverTextures[i]);

                ctx.Dispose();
                surface.Dispose();
            }
        }

        private void Draw(Context ctx, EventCard card, int i, double w, double h, bool hovered)
        {
            if (card.IsHeader) { DrawHeader(ctx, card, w, h); return; }

            DrawBody(ctx, card, w, h, hovered);

            double padX = scaled(UnscaledPadding) + scaled(UnscaledStripeWidth + 6);
            double padY = scaled(UnscaledPadding);
            double textWidth = w - padX - scaled(UnscaledPadding);

            // Drawn as an icon: the game font has no star glyph.
            double titleX = padX;
            if (!string.IsNullOrEmpty(card.TitleIcon))
            {
                double iconSize = scaled(14);
                var accent = card.Accent ?? Palette.Title;
                api.Gui.Icons.DrawIcon(ctx, card.TitleIcon, titleX, padY + scaled(2),
                    iconSize, iconSize, new double[] { accent[0], accent[1], accent[2], 1 });
                titleX += iconSize + scaled(5);
            }

            Font = TitleFontFor(card);
            double titleHeight = textUtil.AutobreakAndDrawMultilineTextAt(
                ctx, Font, card.Title, titleX, padY, textWidth - (titleX - padX));

            if (!string.IsNullOrEmpty(card.Detail))
            {
                Font = DetailFont;
                textUtil.AutobreakAndDrawMultilineTextAt(
                    ctx, Font, card.Detail, padX, padY + titleHeight + scaled(4), textWidth);
            }

            if (card.Progress.HasValue) DrawFillBar(ctx, card, w, h);
            if (!string.IsNullOrEmpty(card.BadgeText)) DrawButton(ctx, card, w, h, hovered);
        }

        private void DrawHeader(Context ctx, EventCard card, double w, double h)
        {
            Font = TitleFontFor(card);
            double textW = Font.GetTextExtents(card.Title).Width;
            textUtil.AutobreakAndDrawMultilineTextAt(ctx, Font, card.Title, 0, h / 2 - scaled(11), w);

            double lineX = textW + scaled(10);
            if (lineX >= w) return;

            ctx.LineWidth = scaled(1);
            ctx.SetSourceRGBA(Palette.Header[0], Palette.Header[1], Palette.Header[2], 0.25);
            ctx.NewPath();
            ctx.MoveTo(lineX, h / 2);
            ctx.LineTo(w, h / 2);
            ctx.Stroke();
        }

        private void DrawBody(Context ctx, EventCard card, double w, double h, bool hovered)
        {
            double radius = scaled(3);
            double lift = hovered ? 0.06 : 0;

            GuiElement.RoundRectangle(ctx, 0, 0, w, h, radius);
            ctx.SetSourceRGBA(0.10 + lift, 0.085 + lift, 0.065 + lift, 0.92);
            ctx.Fill();

            GuiElement.RoundRectangle(ctx, 0, 0, w, h, radius);
            using (var sheen = new LinearGradient(0, 0, 0, h))
            {
                sheen.AddColorStop(0, new Color(1, 0.95, 0.85, hovered ? 0.10 : 0.06));
                sheen.AddColorStop(0.5, new Color(1, 0.95, 0.85, 0));
                ctx.SetSource(sheen);
                ctx.Fill();
            }

            // Accent stripe
            var accent = card.Accent ?? Palette.Title;
            GuiElement.RoundRectangle(ctx, 0, 0, scaled(UnscaledStripeWidth), h, radius);
            ctx.SetSourceRGBA(accent[0], accent[1], accent[2], hovered ? 1.0 : 0.85);
            ctx.Fill();

            // Border
            GuiElement.RoundRectangle(ctx, 0.5, 0.5, w - 1, h - 1, radius);
            ctx.LineWidth = scaled(1);
            ctx.SetSourceRGBA(1, 0.92, 0.7, hovered ? 0.30 : 0.13);
            ctx.Stroke();
        }

        // Plain text when the tile isn't clickable.
        private void DrawButton(Context ctx, EventCard card, double w, double h, bool hovered)
        {
            double bx = scaled(UnscaledPadding) + scaled(UnscaledStripeWidth + 6);
            double bh = scaled(UnscaledButtonHeight);
            double by = h - scaled(UnscaledPadding) - bh;
            double bw = w - bx - scaled(UnscaledPadding);
            var accent = card.Accent ?? Palette.Title;

            if (card.OnClick != null)
            {
                GuiElement.RoundRectangle(ctx, bx, by, bw, bh, scaled(3));
                ctx.SetSourceRGBA(accent[0], accent[1], accent[2], hovered ? 0.55 : 0.32);
                ctx.Fill();

                GuiElement.RoundRectangle(ctx, bx + 0.5, by + 0.5, bw - 1, bh - 1, scaled(3));
                ctx.LineWidth = scaled(1);
                ctx.SetSourceRGBA(accent[0], accent[1], accent[2], hovered ? 1.0 : 0.7);
                ctx.Stroke();
            }

            var font = CairoFont.WhiteSmallText().WithOrientation(EnumTextOrientation.Center);
            if (card.OnClick == null) font.Color = new double[] { accent[0], accent[1], accent[2], 0.9 };

            Font = font;
            textUtil.AutobreakAndDrawMultilineTextAt(
                ctx, Font, card.BadgeText, bx, by + scaled(4), bw, EnumTextOrientation.Center);
        }

        // Lobby fill, just above the button.
        private void DrawFillBar(Context ctx, EventCard card, double w, double h)
        {
            double barH = scaled(3);
            double x = scaled(UnscaledStripeWidth + 6);
            double above = string.IsNullOrEmpty(card.BadgeText)
                ? 0
                : scaled(UnscaledButtonHeight) + scaled(4);
            double y = h - scaled(UnscaledPadding) - above - barH - scaled(1);
            double fullW = w - x - scaled(UnscaledPadding);
            var accent = card.Accent ?? Palette.Title;

            GuiElement.RoundRectangle(ctx, x, y, fullW, barH, barH / 2);
            ctx.SetSourceRGBA(1, 1, 1, 0.08);
            ctx.Fill();

            double frac = Math.Clamp(card.Progress.Value, 0, 1);
            if (frac <= 0) return;

            GuiElement.RoundRectangle(ctx, x, y, Math.Max(barH, fullW * frac), barH, barH / 2);
            ctx.SetSourceRGBA(accent[0], accent[1], accent[2], 0.85);
            ctx.Fill();
        }

        public void OnRenderInteractiveElements(ICoreClientAPI api, float deltaTime)
        {
            if (normalTextures[0].TextureId == 0) Compose();

            int hoveredTile = TileAt(api.Input.MouseX, api.Input.MouseY);
            double tileWidth = TileWidth;

            hoveredIconSlot = null;
            for (int i = 0; i < tiles.Length; i++)
            {
                bool hovered = i == hoveredTile && tiles[i].OnClick != null;
                api.Render.Render2DTexturePremultipliedAlpha(
                    (hovered ? hoverTextures[i] : normalTextures[i]).TextureId,
                    (int)TileX(i), (int)Bounds.absY, (int)tileWidth, Bounds.OuterHeightInt, 50f);

                RenderIcons(api, i, deltaTime);
            }

            if (hoveredIconSlot != null)
            {
                tooltipRenderer ??= new ItemstackComponentBase(api);
                tooltipRenderer.RenderItemstackTooltip(
                    hoveredIconSlot, api.Input.MouseX, api.Input.MouseY, deltaTime);
            }
        }

        private void RenderIcons(ICoreClientAPI api, int i, float deltaTime)
        {
            if (iconSlots[i].Count == 0) return;

            double size = scaled(UnscaledIconSize);
            double x = TileX(i) + scaled(UnscaledStripeWidth + 6 + UnscaledPadding);
            double y = Bounds.absY + scaled(iconRowY[i]);
            double maxX = TileX(i) + TileWidth - scaled(UnscaledPadding) - size;

            // TileAt also checks the clip, so scrolled-out icons don't hover.
            int mouseX = api.Input.MouseX, mouseY = api.Input.MouseY;
            bool mayHover = TileAt(mouseX, mouseY) == i;
            double rowStart = x;
            double itemSize = scaled(UnscaledIconItemSize);

            foreach (var slot in iconSlots[i])
            {
                if (x > maxX) { x = rowStart; y += scaled(UnscaledIconRowHeight); }   // wrap; MeasureTile reserved the rows

                // Stacked scissor intersects with the list's clip.
                api.Render.PushScissor(IconScissor(x, y, size), true);
                api.Render.RenderItemstackToGui(slot, x + size / 2, y + size / 2, 100, (float)itemSize,
                    ColorUtil.WhiteArgb, deltaTime, true, false, true);
                api.Render.PopScissor();

                if (mayHover && mouseX >= x && mouseX < x + size && mouseY >= y && mouseY < y + size)
                    hoveredIconSlot = slot;

                x += size + scaled(4);
            }
        }

        // Read at push time, so one instance is reused; unscaled since CalcWorldBounds rescales.
        private readonly ElementBounds iconScissorBounds = ElementBounds.Fixed(0, 0, 0, 0).WithEmptyParent();

        private bool skipReported;

        private ElementBounds IconScissor(double x, double y, double size)
        {
            double s = RuntimeEnv.GUIScale;
            iconScissorBounds.fixedX = x / s;
            iconScissorBounds.fixedY = y / s;
            iconScissorBounds.fixedWidth = size / s;
            iconScissorBounds.fixedHeight = size / s;
            iconScissorBounds.CalcWorldBounds();
            return iconScissorBounds;
        }

        // Must match the wrapping in RenderIcons.
        private int IconRows(int i)
        {
            double step = scaled(UnscaledIconSize + 4);
            double available = TileWidth - scaled(UnscaledStripeWidth + 6 + UnscaledPadding) - scaled(UnscaledPadding);
            int perRow = Math.Max(1, (int)((available + scaled(4)) / step));
            return (iconSlots[i].Count + perRow - 1) / perRow;
        }

        private double TileX(int i) => Bounds.absX + i * (TileWidth + scaled(UnscaledTileGap));

        private int TileAt(int mouseX, int mouseY)
        {
            if (Bounds.PositionInside(mouseX, mouseY) == null) return -1;
            if (!IsPositionInside(mouseX, mouseY)) return -1;   // scrolled out of the clip

            for (int i = 0; i < tiles.Length; i++)
            {
                double x = TileX(i);
                if (mouseX >= x && mouseX < x + TileWidth) return i;
            }
            return -1;
        }

        public void OnMouseUpOnElement(MouseEvent args, int elementIndex)
        {
            int i = TileAt(args.X, args.Y);
            if (i < 0 || tiles[i].OnClick == null) return;

            api.Gui.PlaySound("menubutton_press");
            tiles[i].OnClick();
            args.Handled = true;
        }

        public void OnMouseDownOnElement(MouseEvent args, int elementIndex) { }
        public void OnMouseMoveOnElement(MouseEvent args, int elementIndex) { }

        public override void Dispose()
        {
            base.Dispose();
            for (int i = 0; i < tiles.Length; i++)
            {
                normalTextures[i]?.Dispose();
                hoverTextures[i]?.Dispose();
            }
            tooltipRenderer?.Dispose();
        }

        private static CairoFont TitleFontFor(EventCard card) => card.IsHeader
            ? CairoFont.WhiteSmallText().WithColor(Palette.Header).WithWeight(Cairo.FontWeight.Bold)
            : CairoFont.WhiteSmallishText().WithColor(card.Accent ?? Palette.Title);

        private static CairoFont DetailFont
        {
            get
            {
                var font = CairoFont.WhiteSmallText();
                font.Color = Palette.Detail;
                font.LineHeightMultiplier = 1.15;
                return font;
            }
        }
    }

    public class EventCardRow
    {
        public EventCard[] Tiles;

        // For narrow dialogs; the browser's odd last tile stays half width.
        public bool FullWidth;

        public static EventCardRow Single(EventCard card)
            => new EventCardRow { Tiles = new[] { card }, FullWidth = true };
    }

    public class EventCard
    {
        public string Title;
        public string Detail;
        public string BadgeText;
        public double[] Accent;
        public bool IsHeader;
        public Action OnClick;

        // 0..1, null for no bar.
        public double? Progress;

        public List<ItemStack> Icons;

        // Engine icon name, e.g. "wpStar1".
        public string TitleIcon;
    }

    public static class Palette
    {
        public static readonly double[] Title  = { 1.00, 0.83, 0.42, 1 };   // brass
        public static readonly double[] Header = { 0.85, 0.78, 0.62, 1 };   // muted parchment
        public static readonly double[] Detail = { 0.78, 0.74, 0.68, 0.85 };
        public static readonly double[] Go     = { 0.47, 0.78, 0.36, 1 };   // joinable / ready / win
        public static readonly double[] Live   = { 0.98, 0.62, 0.22, 1 };   // running right now
        public static readonly double[] Stop   = { 0.90, 0.35, 0.32, 1 };   // leave / danger / loss
        public static readonly double[] Idle   = { 0.62, 0.60, 0.56, 1 };   // waiting, nothing to do
        public static readonly double[] Info   = { 0.45, 0.72, 0.95, 1 };   // ratings, neutral figures

        // Lighter than dialogs, since HUD panels sit over the world.
        public static readonly double[] HudBg  = { 0.06, 0.05, 0.04, 0.55 };
    }
}
