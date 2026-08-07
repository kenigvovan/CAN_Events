using System;
using System.Collections.Generic;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace canevents.src.Gui.Native
{
    /// <summary>
    /// One row of the browser's list: a full-width section header, or up to two event tiles side by
    /// side. The vanilla <c>GuiElementCellList</c> only stacks cells vertically, so the two-column
    /// grid is built a row at a time; both tiles in a row take the taller one's height.
    ///
    /// A tile is drawn once into a texture (twice, counting the hover version); item icons are real
    /// item renders and go through the render pass instead.
    /// </summary>
    public class EventTileRow : GuiElementTextBase, IGuiElementCell
    {
        public const double UnscaledPadding = 10;
        public const double UnscaledStripeWidth = 4;
        public const double UnscaledTileGap = 10;
        public const double UnscaledButtonHeight = 26;
        // Cell pitch vs item render size mirrors vanilla's slot grid (48px slot, 25.6 item): the size
        // parameter scales the MODEL, not a bounding box, so a long mesh (a spear) sprawls across its
        // neighbours. The vanilla ratio plus a per-icon scissor keeps each item in its own cell.
        public const double UnscaledIconSize = 30;
        public const double UnscaledIconItemSize = 17;
        public const double UnscaledIconRowHeight = 34;

        private readonly EventCard[] tiles;
        private readonly bool fullWidth;
        private readonly List<DummySlot>[] iconSlots;
        private readonly LoadedTexture[] normalTextures;
        private readonly LoadedTexture[] hoverTextures;
        private readonly double[] iconRowY;      // unscaled, relative to the tile's top

        // The vanilla itemstack tooltip, created lazily since most rows are never hovered. Rendered
        // via ItemstackComponentBase, which handles screen-edge flipping and lifts the scissor -
        // otherwise the list's clip would cut the box off.
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

        /// <summary>Width of a single tile, in scaled pixels. A header row is one full-width tile.</summary>
        private double TileWidth => IsHeaderRow || fullWidth
            ? Bounds.InnerWidth
            : (Bounds.InnerWidth - scaled(UnscaledTileGap)) / 2;

        private bool IsHeaderRow => tiles[0].IsHeader;

        public void UpdateCellHeight()
        {
            Bounds.CalcWorldBounds();

            double height = 0;
            for (int i = 0; i < tiles.Length; i++) height = Math.Max(height, MeasureTile(i));

            // Both tiles in a row share the taller one's height, which keeps the columns lined up and
            // is why the button is bottom-anchored rather than placed after the text.
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

            // An engine icon in front of the title, drawn into Cairo rather than put in the string:
            // the game font has no star glyph and prints a box.
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

        // Section label: text plus a rule to the right edge, cheaper than a full tile of height.
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

            // A faint top-down sheen: flat fills read as placeholder art.
            GuiElement.RoundRectangle(ctx, 0, 0, w, h, radius);
            using (var sheen = new LinearGradient(0, 0, 0, h))
            {
                sheen.AddColorStop(0, new Color(1, 0.95, 0.85, hovered ? 0.10 : 0.06));
                sheen.AddColorStop(0.5, new Color(1, 0.95, 0.85, 0));
                ctx.SetSource(sheen);
                ctx.Fill();
            }

            // Accent stripe: the tile's state, readable before any word is.
            var accent = card.Accent ?? Palette.Title;
            GuiElement.RoundRectangle(ctx, 0, 0, scaled(UnscaledStripeWidth), h, radius);
            ctx.SetSourceRGBA(accent[0], accent[1], accent[2], hovered ? 1.0 : 0.85);
            ctx.Fill();

            // Hairline border, brighter on hover so the pointer target is obvious.
            GuiElement.RoundRectangle(ctx, 0.5, 0.5, w - 1, h - 1, radius);
            ctx.LineWidth = scaled(1);
            ctx.SetSourceRGBA(1, 0.92, 0.7, hovered ? 0.30 : 0.13);
            ctx.Stroke();
        }

        // The action button, or a bare state word when the tile is not clickable. Full width along
        // the bottom: a half-width tile has no room for a right-hand pill, and anchoring to the
        // bottom stops it floating mid-tile in the shorter of two tiles sharing a height.
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

        // How full the lobby is, right above the button (or along the bottom when there is none).
        // Bottom-anchored for the same reason the button is.
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

        // Real item renders rather than names - the language the rest of the game speaks.
        private void RenderIcons(ICoreClientAPI api, int i, float deltaTime)
        {
            if (iconSlots[i].Count == 0) return;

            double size = scaled(UnscaledIconSize);
            double x = TileX(i) + scaled(UnscaledStripeWidth + 6 + UnscaledPadding);
            double y = Bounds.absY + scaled(iconRowY[i]);
            double maxX = TileX(i) + TileWidth - scaled(UnscaledPadding) - size;

            // Hover detection piggybacks on the draw loop, which already knows every icon's rect.
            // TileAt(...) doubles as the clip check, so icons scrolled out of view stay inert.
            int mouseX = api.Input.MouseX, mouseY = api.Input.MouseY;
            bool mayHover = TileAt(mouseX, mouseY) == i;
            double rowStart = x;
            double itemSize = scaled(UnscaledIconItemSize);

            foreach (var slot in iconSlots[i])
            {
                if (x > maxX) { x = rowStart; y += scaled(UnscaledIconRowHeight); }   // wrap; MeasureTile reserved the rows

                // Stacking scissor: intersected with the list's clip, so an icon on a half-scrolled
                // row still vanishes at the viewport edge.
                api.Render.PushScissor(IconScissor(x, y, size), true);
                api.Render.RenderItemstackToGui(slot, x + size / 2, y + size / 2, 100, (float)itemSize,
                    ColorUtil.WhiteArgb, deltaTime, true, false, true);
                api.Render.PopScissor();

                if (mayHover && mouseX >= x && mouseX < x + size && mouseY >= y && mouseY < y + size)
                    hoveredIconSlot = slot;

                x += size + scaled(4);
            }
        }

        // Reused for every icon: scissor bounds are read at push time, so one instance can be
        // repositioned per call. Screen pixels go back to unscaled, since CalcWorldBounds rescales.
        private readonly ElementBounds iconScissorBounds = ElementBounds.Fixed(0, 0, 0, 0).WithEmptyParent();

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

        /// <summary>How many rows the tile's icons wrap onto, from the same geometry RenderIcons uses.</summary>
        private int IconRows(int i)
        {
            double step = scaled(UnscaledIconSize + 4);
            double available = TileWidth - scaled(UnscaledStripeWidth + 6 + UnscaledPadding) - scaled(UnscaledPadding);
            int perRow = Math.Max(1, (int)((available + scaled(4)) / step));
            return (iconSlots[i].Count + perRow - 1) / perRow;
        }

        private double TileX(int i) => Bounds.absX + i * (TileWidth + scaled(UnscaledTileGap));

        /// <summary>Index of the tile under the given screen position, or -1 (also for the gap).</summary>
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

    /// <summary>One list row: a full-width header, or one or two tiles side by side.</summary>
    public class EventCardRow
    {
        public EventCard[] Tiles;

        /// <summary>
        /// Single tile spanning the whole row, for the narrow dialogs (class pick). The browser's
        /// trailing odd tile keeps its half width instead, so the grid stays a grid.
        /// </summary>
        public bool FullWidth;

        /// <summary>Convenience for the one-tile-per-row lists.</summary>
        public static EventCardRow Single(EventCard card)
            => new EventCardRow { Tiles = new[] { card }, FullWidth = true };
    }

    /// <summary>The data behind one tile.</summary>
    public class EventCard
    {
        public string Title;
        public string Detail;
        public string BadgeText;
        public double[] Accent;
        public bool IsHeader;
        public Action OnClick;

        /// <summary>0..1 fill bar above the button (lobby headcount). Null draws no bar.</summary>
        public double? Progress;

        /// <summary>Items to show as icons under the text (fees, prizes, loadout contents).</summary>
        public List<ItemStack> Icons;

        /// <summary>
        /// Engine icon drawn in front of the title, by name ("wpStar1"). A Cairo vector, not a
        /// unicode star: the game font has no glyph for one and renders an empty box.
        /// </summary>
        public string TitleIcon;
    }

    /// <summary>
    /// One palette for every native dialog: warm parchment-on-dark, matching the game's own.
    /// </summary>
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

        /// <summary>HUD panel backing. Lighter than a dialog's: it sits over the world all match.</summary>
        public static readonly double[] HudBg  = { 0.06, 0.05, 0.04, 0.55 };
    }
}
