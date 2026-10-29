using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace canevents.src.Gui.Admin.Dialogs
{
    // Side window composer plus a row cursor walking down it.
    public sealed class DialogLayout
    {
        public ICoreClientAPI Capi;
        public GuiComposer Compo;
        public ElementBounds Bg;
        public ElementBounds Row;

        private int keyCounter;

        public string NextKey(string prefix) => "side-" + prefix + "-" + (keyCounter++);

        // Rows are 30 (one line); grow for wrapped text so the next row doesn't overlap. Never shrinks.
        public void FitRowToText(string text, CairoFont font)
        {
            if (string.IsNullOrEmpty(text)) return;
            double width = Row.fixedWidth > 0 ? Row.fixedWidth : 180;
            // Engine measures scaled pixels; rows are unscaled.
            double height = Capi.Gui.Text.GetMultilineTextHeight(font, text, GuiElement.scaled(width))
                            / RuntimeEnv.GUIScale;
            if (height > Row.fixedHeight) Row.fixedHeight = height;
        }

        // Already added as a child of Bg.
        public ElementBounds NextRow(double gap = 12, double? width = null, double? height = null)
        {
            ElementBounds next = Row.BelowCopy(0, gap);
            if (width.HasValue) next.WithFixedWidth(width.Value);
            if (height.HasValue) next.WithFixedHeight(height.Value);
            Bg.WithChildren(next);
            Row = next;
            return next;
        }

        // Row keeps its full width: halving Row itself made every later row half as wide again.
        public ElementBounds SplitRow(out ElementBounds right, double gap = 12)
        {
            ElementBounds row = NextRow(gap);
            ElementBounds left = row.FlatCopy();
            left.fixedWidth = (row.fixedWidth - 8) / 2;
            right = left.RightCopy(8, 0);
            Bg.WithChildren(left, right);
            return left;
        }
    }

    internal static class DialogFrame
    {
        public const string ComposerKey = "canevents-admin-side";

        public static DialogLayout Create(AdminPanelGui gui, double width)
        {
            ICoreClientAPI capi = gui.Capi;
            ElementBounds mainDlg = gui.MainDialogBounds;

            ElementBounds bgBounds = ElementBounds.Fixed(0.0, 0.0, width, 60)
                .WithFixedPadding(GuiStyle.ElementToDialogPadding);
            bgBounds.BothSizing = ElementSizing.FitToChildren;

            // Right of the panel, tops aligned. render* are scaled, the offset is unscaled.
            ElementBounds dialogBounds = bgBounds.ForkBoundingParent(0.0, 0.0, 0.0, 0.0)
                .WithAlignment(EnumDialogArea.None)
                .WithFixedAlignmentOffset((mainDlg.renderX + mainDlg.OuterWidth + 10.0) / RuntimeEnv.GUIScale,
                                          mainDlg.renderY / RuntimeEnv.GUIScale);
            dialogBounds.BothSizing = ElementSizing.FitToChildren;

            ElementBounds textBounds = ElementBounds.FixedPos(EnumDialogArea.LeftTop, 0, 0);
            bgBounds.WithChildren(textBounds);

            var compo = capi.Gui.CreateCompo(ComposerKey, dialogBounds)
                                .AddShadedDialogBG(bgBounds, true, 5.0, 0.85f);

            ElementBounds firstRow = textBounds.CopyOffsetedSibling().WithFixedSize(width, 30);
            bgBounds.WithChildren(firstRow);
            firstRow.fixedY += 20;

            return new DialogLayout { Capi = capi, Compo = compo, Bg = bgBounds, Row = firstRow };
        }
    }
}
