using Cairo;
using canevents.src.Gui.Admin.Widgets;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace canevents.src.Gui.Admin.GuiElements
{
    public class SettingsCell : CANGuiElementCellBase
    {
        private const double EdgePadding = 10;
        private const double RowHeight = 26;
        private const double GroupHeight = 30;
        private const double ValueWidth = 140;

        private readonly bool isGroup;
        private readonly bool isEditing;

        protected override int ClickZones => 1;

        protected override double MinCellHeight => isGroup ? GroupHeight : RowHeight;

        protected override bool ShowModifyIcons => false;

        protected override bool UseHoverHighlights => !isGroup;

        public SettingsCell(ICoreClientAPI capi, SettingRow row, ElementBounds bounds, bool isEditing)
            : base(capi, bounds)
        {
            this.isGroup = row.Kind == SettingKind.Group;
            this.isEditing = isEditing;

            double width = Bounds.fixedWidth > 0 ? Bounds.fixedWidth : 430;

            if (isGroup)
            {
                var titleBounds = ElementBounds.Fixed(EdgePadding, 5, width - EdgePadding * 2, 22).WithParent(Bounds);
                AddText(row.Label, titleBounds, AdminColors.Section, 17);
            }
            else
            {
                var nameBounds = ElementBounds
                    .Fixed(EdgePadding, 4, width - ValueWidth - EdgePadding * 2, 20).WithParent(Bounds);
                AddText(row.Label, nameBounds, AdminColors.Label, 14);

                var valueBounds = ElementBounds
                    .Fixed(width - ValueWidth - EdgePadding, 4, ValueWidth, 20).WithParent(Bounds);
                var font = CairoFont.WhiteSmallText();
                AddText(Card.Fit(row.DisplayValue(), font, ValueWidth), valueBounds, ColorFor(row), 14);

                string hint = Lang.Get(row.Kind == SettingKind.Flag
                    ? "canevents:admin-settings-click-flag"
                    : row.Kind == SettingKind.Choice
                        ? "canevents:admin-settings-click-choice"
                        : "canevents:admin-settings-click-edit");
                string range = row.RangeText();
                string tip = (row.Tooltip != null ? row.Tooltip + "\n" : "") + hint + (range != null ? "\n" + range : "");
                AddTooltip(ElementBounds.Fixed(0, 0, width, RowHeight).WithParent(Bounds), tip);
            }

            Bounds.fixedHeight = MinCellHeight;
        }

        private static double[] ColorFor(SettingRow row)
        {
            if (row.Kind != SettingKind.Flag) return AdminColors.Value;
            return row.GetFlag() ? AdminColors.Success : AdminColors.Danger;
        }

        // Text is drawn by the base; only the edit tint here.
        protected override void ComposeContent(Context ctx, ImageSurface surface)
        {
            if (!isEditing) return;
            GuiElement.RoundRectangle(ctx, 0, 0, Bounds.OuterWidth, Bounds.OuterHeight, GuiElement.scaled(3));
            ctx.SetSourceRGBA(AdminColors.Value[0], AdminColors.Value[1], AdminColors.Value[2], 0.14);
            ctx.Fill();
        }
    }
}
