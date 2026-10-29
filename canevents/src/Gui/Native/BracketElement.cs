using System;
using System.Collections.Generic;
using System.Linq;
using Cairo;
using canevents.src.Tournaments;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace canevents.src.Gui.Native
{
    // Drawn once into a Cairo texture (plus a hover variant), blitted per frame. Geometry from BracketLayout.
    public class BracketElement : GuiElement
    {
        private readonly BracketView view;
        private readonly BracketLayout.Layout layout;
        // Not readonly: generateTexture takes them by ref.
        private LoadedTexture texture;
        private LoadedTexture hoverTexture;

        public const double DefaultBoxWidth = 190;
        // Below this the view pans instead of shrinking.
        public const double MinBoxWidth = 120;
        public const double BoxH = 46;
        public const double HGap = 46;
        public const double VGap = 14;
        public const double Margin = 8;

        private int hoveredMatchId = -1;
        private int composedHoverId = -1;

        public Action<int> OnMatchClicked;

        // Null: a right click is a plain click.
        public Action<int> OnMatchRightClicked;

        private readonly double fontScale;

        public BracketElement(ICoreClientAPI capi, BracketView view, ElementBounds bounds,
            BracketLayout.Layout layout = null, double fontScale = 1) : base(capi, bounds)
        {
            this.view = view;
            this.fontScale = Math.Max(0.5, fontScale);
            this.layout = layout ?? BracketLayout.Compute(view);
            texture = new LoadedTexture(capi);
            hoverTexture = new LoadedTexture(capi);
        }

        // Unscaled.
        public double ContentWidth => layout.Width + Margin * 2;
        public double ContentHeight => layout.Height + Margin * 2;

        public override void ComposeElements(Context ctxStatic, ImageSurface surfaceStatic)
        {
            Bounds.CalcWorldBounds();
            Recompose();
        }

        private void Recompose()
        {
            int w = Math.Max(1, (int)Bounds.InnerWidth);
            int h = Math.Max(1, (int)Bounds.InnerHeight);

            var surface = new ImageSurface(Format.Argb32, w, h);
            var ctx = new Context(surface);

            Draw(ctx, -1);
            generateTexture(surface, ref texture);

            ctx.Operator = Operator.Clear;
            ctx.Paint();
            ctx.Operator = Operator.Over;
            Draw(ctx, hoveredMatchId);
            generateTexture(surface, ref hoverTexture);
            composedHoverId = hoveredMatchId;

            ctx.Dispose();
            surface.Dispose();
        }

        private void Draw(Context ctx, int hoverId)
        {
            double ox = scaled(Margin);
            double oy = scaled(Margin);

            // Connectors first so boxes draw over them.
            ctx.LineWidth = scaled(2);
            ctx.SetSourceRGBA(0.85, 0.78, 0.62, 0.30);
            foreach (var e in layout.Edges)
            {
                ctx.NewPath();
                ctx.MoveTo(ox + scaled(e.X0), oy + scaled(e.Y0));
                ctx.LineTo(ox + scaled(e.XMid), oy + scaled(e.Y0));
                ctx.LineTo(ox + scaled(e.XMid), oy + scaled(e.Y1));
                ctx.LineTo(ox + scaled(e.X1), oy + scaled(e.Y1));
                ctx.Stroke();
            }

            foreach (var node in layout.Nodes)
            {
                var m = view.Matches.FirstOrDefault(x => x.Id == node.MatchId);
                if (m == null) continue;
                DrawNode(ctx, m, node, ox, oy, node.MatchId == hoverId);
            }
        }

        private void DrawNode(Context ctx, BracketView.Match m, BracketLayout.Node node,
            double ox, double oy, bool hovered)
        {
            double x = ox + scaled(node.X);
            double y = oy + scaled(node.Y);
            double w = scaled(node.W);
            double h = scaled(node.H);
            double radius = scaled(3);
            double lift = hovered ? 0.06 : 0;

            RoundRectangle(ctx, x, y, w, h, radius);
            ctx.SetSourceRGBA(0.10 + lift, 0.085 + lift, 0.065 + lift, 0.92);
            ctx.Fill();

            var accent = AccentFor(m);
            RoundRectangle(ctx, x, y, scaled(4), h, radius);
            ctx.SetSourceRGBA(accent[0], accent[1], accent[2], hovered ? 1.0 : 0.85);
            ctx.Fill();

            RoundRectangle(ctx, x + 0.5, y + 0.5, w - 1, h - 1, radius);
            ctx.LineWidth = scaled(1);
            ctx.SetSourceRGBA(1, 0.92, 0.7, hovered ? 0.30 : 0.13);
            ctx.Stroke();

            double textX = x + scaled(8 * fontScale);
            double textW = w - scaled(14 * fontScale);

            int rows = Math.Max(2, m.Slots.Count);
            double rowH = h / rows;
            for (int i = 0; i < rows; i++)
            {
                string side = i < m.Slots.Count ? m.Slots[i] : null;
                DrawSide(ctx, m, side, textX, y + rowH * i, rowH, textW);
            }
        }

        private void DrawSide(Context ctx, BracketView.Match m, string entrantId,
            double x, double rowTop, double rowH, double w)
        {
            bool decided = !string.IsNullOrEmpty(m.Winner);
            bool lost = (decided && m.Winner != entrantId) || m.IsAbsent(entrantId);

            var font = CairoFont.WhiteSmallText();
            font.UnscaledFontsize *= fontScale;
            if (lost || string.IsNullOrEmpty(entrantId))
                font.Color = new double[] { 0.62, 0.60, 0.55, 0.85 };

            // Empty slot = undecided; a forfeit keeps its name, marked as no-show.
            string text = string.IsNullOrEmpty(entrantId)
                ? Vintagestory.API.Config.Lang.Get("canevents:gui-tournament-tbd")
                : m.IsAbsent(entrantId)
                    ? LabelFor(entrantId) + " " + Vintagestory.API.Config.Lang.Get("canevents:gui-tournament-noshow")
                    : LabelFor(entrantId);

            font.SetupContext(ctx);
            var extents = font.GetFontExtents();
            double baseline = rowTop + (rowH - (extents.Ascent + extents.Descent)) / 2 + extents.Ascent;
            ctx.MoveTo(x, baseline);
            ctx.ShowText(Clip(font, text, w));
        }

        private string LabelFor(string entrantId)
        {
            int seed = view.SeedOf(entrantId);
            string name = view.NameOf(entrantId) ?? "?";
            return seed > 0 ? seed + ". " + name : name;
        }

        private static string Clip(CairoFont font, string text, double maxWidth)
        {
            if (font.GetTextExtents(text).Width <= maxWidth) return text;
            for (int len = text.Length - 1; len > 1; len--)
            {
                string candidate = text.Substring(0, len) + "...";
                if (font.GetTextExtents(candidate).Width <= maxWidth) return candidate;
            }
            return text;
        }

        private static double[] AccentFor(BracketView.Match m) => BracketPalette.ColorOf(m.State);

        public override void RenderInteractiveElements(float deltaTime)
        {
            int wanted = MatchAt(api.Input.MouseX, api.Input.MouseY);
            if (wanted != hoveredMatchId)
            {
                hoveredMatchId = wanted;
                if (hoveredMatchId >= 0 || composedHoverId >= 0) Recompose();
            }

            var tex = hoveredMatchId >= 0 ? hoverTexture : texture;
            api.Render.Render2DTexturePremultipliedAlpha(
                tex.TextureId, (int)Bounds.renderX, (int)Bounds.renderY,
                (int)Bounds.InnerWidth, (int)Bounds.InnerHeight, 50f);
        }

        // -1 if none. Also the clip check for a scrolled node.
        private int MatchAt(int mouseX, int mouseY)
        {
            if (Bounds.PositionInside(mouseX, mouseY) == null) return -1;

            double localX = mouseX - Bounds.renderX - scaled(Margin);
            double localY = mouseY - Bounds.renderY - scaled(Margin);

            foreach (var node in layout.Nodes)
            {
                double x = scaled(node.X), y = scaled(node.Y);
                if (localX >= x && localX < x + scaled(node.W)
                 && localY >= y && localY < y + scaled(node.H))
                    return node.MatchId;
            }
            return -1;
        }

        public override void OnMouseUpOnElement(ICoreClientAPI api, MouseEvent args)
        {
            int id = MatchAt(args.X, args.Y);
            if (id < 0) return;
            api.Gui.PlaySound("menubutton_press");
            // Right click handler is set by the admin panel's result menu.
            if (args.Button == Vintagestory.API.Common.EnumMouseButton.Right && OnMatchRightClicked != null)
                OnMatchRightClicked(id);
            else
                OnMatchClicked?.Invoke(id);
            args.Handled = true;
        }

        public override void Dispose()
        {
            base.Dispose();
            texture?.Dispose();
            hoverTexture?.Dispose();
        }
    }
}
