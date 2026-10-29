using Vintagestory.API.Client;

namespace canevents.src.Gui.Admin.Widgets
{
    public static class AdminFonts
    {
        // Stat lines at the top of most pages.
        public static CairoFont PageLabel =>
            CairoFont.ButtonText().WithFontSize(20).WithOrientation(EnumTextOrientation.Left);

        public static CairoFont ListHeader =>
            CairoFont.WhiteMediumText().WithOrientation(EnumTextOrientation.Center);

        public static CairoFont Body => CairoFont.WhiteSmallText();

        public static CairoFont Hint => CairoFont.WhiteSmallText().WithColor(AdminColors.Muted);
    }
}
