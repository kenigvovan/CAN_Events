using canevents.src.Gui.Admin.Widgets;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace canevents.src.Gui.Admin.Pages
{
    // Page with its own sub-tab strip; the selection is kept in AdminGuiState.SubTabs.
    public abstract class AdminPageBase : CANGuiPage
    {
        private const double StripHeight = 26;

        protected abstract PanelTab Tab { get; }

        protected abstract string[] SubTabLangKeys { get; }

        protected int SubTab
        {
            get
            {
                int i = State.SubTabs.TryGetValue(Tab, out int v) ? v : 0;
                int count = SubTabLangKeys.Length;
                return count == 0 ? 0 : System.Math.Clamp(i, 0, count - 1);
            }
            set => State.SubTabs[Tab] = value;
        }

        protected sealed override void BuildContent(PageBuildContext ctx)
        {
            var compo = ctx.Compo;
            string[] keys = SubTabLangKeys;

            GuiTab[] tabs = new GuiTab[keys.Length];
            for (int i = 0; i < keys.Length; i++)
            {
                tabs[i] = new GuiTab { Name = Lang.Get(keys[i]), DataInt = i };
            }

            var stripBounds = ctx.Current.FlatCopy().WithFixedHeight(StripHeight);
            stripBounds.fixedY += 10;

            var font = CairoFont.WhiteDetailText();
            compo.AddHorizontalTabs(tabs, stripBounds, value =>
            {
                if (value < 0 || value >= keys.Length || value == SubTab) return;
                SubTab = value;
                OnSubTabChanged(value);
                Rebuild();
            }, font, font.Clone().WithColor(GuiStyle.ActiveButtonTextColor), "subtabs");

            compo.GetHorizontalTabs("subtabs").activeElement = SubTab;

            ctx.Current = stripBounds.BelowCopy(0, 6).WithFixedHeight(0);
            BuildSubTab(ctx, SubTab);
        }

        protected abstract void BuildSubTab(PageBuildContext ctx, int subTab);

        // Runs before the rebuild; use it for data requests.
        protected virtual void OnSubTabChanged(int subTab) { }

        protected static double ButtonWidth(string caption) => ButtonRow.ButtonWidth(caption);

        protected static double TextHeight(string text, double width, double lineHeight = 18)
            => ButtonRow.TextHeight(text, width, lineHeight);
    }
}
