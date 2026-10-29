using System;
using System.Collections.Generic;
using Cairo;
using canevents.src.Gui.Admin.Widgets;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace canevents.src.Gui.Admin.GuiElements
{
    public sealed class ColumnDef
    {
        // Already translated; null = empty header cell.
        public string Header;

        // Unscaled. <= 0 means fill the remaining width (first such column only).
        public double Width;

        public EnumTextOrientation Align = EnumTextOrientation.Left;

        public ColumnDef(string header, double width, EnumTextOrientation align = EnumTextOrientation.Left)
        {
            Header = header;
            Width = width;
            Align = align;
        }
    }

    // Shared by the header row and every cell so columns line up.
    public sealed class ColumnSpec
    {
        public ColumnDef[] Columns;

        // Unscaled.
        public double RowHeight = 28;

        public double Gap = 8;

        public ColumnSpec(params ColumnDef[] columns)
        {
            Columns = columns;
        }

        public (double x, double w)[] Layout(double totalWidth)
        {
            var result = new (double, double)[Columns.Length];
            double fixedSum = Gap;
            int fill = -1;
            for (int i = 0; i < Columns.Length; i++)
            {
                if (Columns[i].Width > 0) fixedSum += Columns[i].Width + Gap;
                else if (fill < 0) { fill = i; fixedSum += Gap; }
                else fixedSum += Gap;
            }
            double fillWidth = Math.Max(20, totalWidth - fixedSum);

            double x = Gap;
            for (int i = 0; i < Columns.Length; i++)
            {
                double w = Columns[i].Width > 0 ? Columns[i].Width : (i == fill ? fillWidth : 0);
                result[i] = (x, w);
                x += w + Gap;
            }
            return result;
        }
    }

    public sealed class ColumnButton
    {
        public int Column;
        public string Caption;
        public Action OnClick;
        public string Tooltip;

        // Unscaled; 0 = fit caption.
        public double Width;

        public bool Enabled = true;
    }

    public sealed class ColumnRow
    {
        public string[] Texts;

        // null entries use AdminColors.Value.
        public double[][] Colors;

        public List<ColumnButton> Buttons = new List<ColumnButton>();

        public List<string> IconCodes;
        public int IconColumn = -1;

        // null = no hover highlight on the row.
        public Action OnClick;

        public string Tooltip;

        public bool Selected;

        public ColumnRow(params string[] texts)
        {
            Texts = texts;
        }

        public ColumnRow WithButton(int column, string caption, Action onClick, string tooltip = null, double width = 0,
                                    bool enabled = true)
        {
            Buttons.Add(new ColumnButton { Column = column, Caption = caption, OnClick = onClick, Tooltip = tooltip, Width = width, Enabled = enabled });
            return this;
        }

        public ColumnRow WithColor(int column, double[] color)
        {
            if (Colors == null || Colors.Length < Texts.Length) Array.Resize(ref Colors, Texts.Length);
            if (column >= 0 && column < Colors.Length) Colors[column] = color;
            return this;
        }

        public string[] CellTooltips;

        public ColumnRow WithCellTooltip(int column, string tooltip)
        {
            if (CellTooltips == null || CellTooltips.Length < Texts.Length) Array.Resize(ref CellTooltips, Texts.Length);
            if (column >= 0 && column < CellTooltips.Length) CellTooltips[column] = tooltip;
            return this;
        }
    }

    public class ColumnCell : CANGuiElementCellBase
    {
        private const double ButtonHeight = 22;
        private const double ButtonGap = 8;

        private readonly ColumnSpec spec;
        private readonly ColumnRow row;
        private readonly (double x, double w)[] layout;
        private readonly List<DummySlot> iconSlots = new List<DummySlot>();
        private readonly ItemIconRenderer icons;

        protected override int ClickZones => 1;
        protected override bool UseHoverHighlights => row.OnClick != null;
        protected override bool ShowModifyIcons => false;
        protected override double MinCellHeight => spec.RowHeight;

        public ColumnCell(ICoreClientAPI capi, ColumnSpec spec, ColumnRow row, ElementBounds bounds)
            : base(capi, bounds)
        {
            this.spec = spec;
            this.row = row;
            double width = Bounds.fixedWidth > 0 ? Bounds.fixedWidth : 600;
            layout = spec.Layout(width);

            if (row.OnClick != null) OnMouseDownOnCellLeft = _ => row.OnClick();

            double textY = Math.Max(0, (spec.RowHeight - 18) / 2);
            for (int i = 0; i < layout.Length && row.Texts != null && i < row.Texts.Length; i++)
            {
                string text = row.Texts[i];
                if (string.IsNullOrEmpty(text) || layout[i].w <= 0) continue;

                double[] color = row.Colors != null && i < row.Colors.Length && row.Colors[i] != null
                    ? row.Colors[i] : AdminColors.Value;
                var font = CairoFont.WhiteSmallText().WithColor(color).WithOrientation(spec.Columns[i].Align);
                string shown = Card.Fit(text, font, layout[i].w);
                AddText(shown, ElementBounds.Fixed(layout[i].x, textY, layout[i].w, 18).WithParent(Bounds), color, 14);
                string cellTip = row.CellTooltips != null && i < row.CellTooltips.Length ? row.CellTooltips[i] : null;
                string tip = shown != text ? (cellTip == null ? text : text + "\n" + cellTip) : cellTip;
                if (tip != null) AddTooltip(ElementBounds.Fixed(layout[i].x, 0, layout[i].w, spec.RowHeight).WithParent(Bounds), tip);
            }

            AddButtons();

            if (row.IconCodes != null && row.IconColumn >= 0)
            {
                foreach (var code in row.IconCodes)
                {
                    var slot = ItemIconRenderer.SlotFor(capi, code);
                    if (slot != null) iconSlots.Add(slot);
                }
                if (iconSlots.Count > 0) icons = new ItemIconRenderer(capi);
            }

            if (row.Tooltip != null)
                AddTooltip(ElementBounds.Fixed(0, 0, width, spec.RowHeight).WithParent(Bounds), row.Tooltip);

            Bounds.fixedHeight = MinCellHeight;
        }

        private void AddButtons()
        {
            if (row.Buttons == null) return;
            var nextX = new double[layout.Length];
            for (int i = 0; i < layout.Length; i++) nextX[i] = layout[i].x;

            double y = Math.Max(0, (spec.RowHeight - ButtonHeight) / 2);
            var font = CairoFont.SmallButtonText(EnumButtonStyle.Small);

            foreach (var b in row.Buttons)
            {
                if (b.Column < 0 || b.Column >= layout.Length || b.OnClick == null) continue;

                double w = b.Width > 0 ? b.Width : font.GetTextExtents(b.Caption ?? "").Width + 16;
                var bounds = ElementBounds.Fixed(nextX[b.Column], y, w, ButtonHeight).WithParent(Bounds);
                nextX[b.Column] += w + ButtonGap;

                var pressed = CairoFont.SmallButtonText(EnumButtonStyle.Small);
                pressed.Color = (double[])GuiStyle.ActiveButtonTextColor.Clone();
                Action click = b.OnClick;
                var button = new GuiElementTextButton(capi, b.Caption ?? "", font.Clone(), pressed,
                    new ActionConsumable(() => { click(); return true; }), bounds, EnumButtonStyle.Small);
                button.Enabled = b.Enabled;
                children.Add(button);

                if (b.Tooltip != null) AddTooltip(bounds, b.Tooltip);
            }
        }

        protected override void ComposeContent(Context ctx, ImageSurface surface)
        {
            if (!row.Selected) return;
            GuiElement.RoundRectangle(ctx, 0, 0, Bounds.OuterWidth, Bounds.OuterHeight, GuiElement.scaled(3));
            ctx.SetSourceRGBA(AdminColors.Value[0], AdminColors.Value[1], AdminColors.Value[2], 0.14);
            ctx.Fill();
        }

        protected override void RenderOverlay(ICoreClientAPI api, float deltaTime)
        {
            if (icons == null) return;

            var col = layout[row.IconColumn];
            double size = scaled(Math.Min(ItemIconRenderer.UnscaledIconSize, spec.RowHeight - 2));
            double x = Bounds.absX + scaled(col.x);
            double maxX = Bounds.absX + scaled(col.x + col.w) - size;
            double y = Bounds.absY + (Bounds.OuterHeight - size) / 2;

            // No hover for icons scrolled out of the clip.
            bool mayHover = IsPositionInside(api.Input.MouseX, api.Input.MouseY);

            icons.BeginFrame();
            foreach (var slot in iconSlots)
            {
                if (x > maxX) break; // single line, no wrap
                icons.Render(slot, x, y, size, deltaTime, mayHover);
                x += size + scaled(2);
            }
            icons.EndFrame(deltaTime);
        }

        public override void Dispose()
        {
            base.Dispose();
            icons?.Dispose();
        }
    }

    public static class ColumnTable
    {
        public const double HeaderHeight = 20;

        // 28 + 2x2 padding + 4 spacing. Less overflows and spawns a scrollbar that steals the wheel.
        public const double RowPitch = 36;

        public static ScrollableListLayout Add(Pages.PageBuildContext ctx, ElementBounds anchor, ColumnSpec spec,
                                               IList<ColumnRow> rows, double listHeight, ScrollableListOptions opts = null)
        {
            opts = opts ?? new ScrollableListOptions();
            var compo = ctx.Compo;

            // Mirrors ScrollableList's widths (-30 scrollbar, inset, padding) so headers line up.
            double listWidth = anchor.fixedWidth - 30;
            double cellWidth = listWidth - 2 * 5 - 2 * opts.CellHorPadding;
            double cellLeft = 3 + 5 + opts.CellHorPadding;

            var layout = spec.Layout(cellWidth);
            var font = CairoFont.WhiteSmallText().WithColor(AdminColors.Label);
            for (int i = 0; i < layout.Length; i++)
            {
                string header = spec.Columns[i].Header;
                if (string.IsNullOrEmpty(header) || layout[i].w <= 0) continue;
                var b = ElementBounds.Fixed(anchor.fixedX + cellLeft + layout[i].x, anchor.fixedY, layout[i].w, HeaderHeight);
                compo.AddStaticText(Card.Fit(header, font, layout[i].w), font.Clone().WithOrientation(spec.Columns[i].Align),
                    b, opts.Key + "-head-" + i);
            }

            var container = ElementBounds.Fixed(anchor.fixedX, anchor.fixedY + HeaderHeight + 4, anchor.fixedWidth, 0);
            opts.Container = container;
            opts.HeightReserve = ScrollableList.ReserveFor(ctx.Main, listHeight);

            return ScrollableList.Add(ctx, container, null, rows,
                (ColumnRow row, ElementBounds bounds) => new ColumnCell(ctx.Compo.Api, spec, row, bounds), opts);
        }
    }
}
