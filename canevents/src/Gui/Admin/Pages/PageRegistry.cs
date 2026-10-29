using System.Collections.Generic;

namespace canevents.src.Gui.Admin.Pages
{
    // Pages are created once and reused.
    public sealed class PageRegistry
    {
        private readonly Dictionary<PanelTab, CANGuiPage> pages = new Dictionary<PanelTab, CANGuiPage>();

        public PageRegistry()
        {
            Add(PanelTab.Help, new HelpPage());
            Add(PanelTab.Players, new PlayersPage());
            Add(PanelTab.Dashboard, new DashboardPage());
            Add(PanelTab.Events, new EventsPage());
            Add(PanelTab.Debug, new DebugPage());
            Add(PanelTab.Arenas, new ArenasPage());
            Add(PanelTab.NewArena, new WizardPage());
            Add(PanelTab.Content, new ContentPage());
            Add(PanelTab.Economy, new EconomyPage());
            Add(PanelTab.Tournaments, new TournamentsPage());
        }

        public void Add(PanelTab tab, CANGuiPage page) => pages[tab] = page;

        public CANGuiPage Get(PanelTab tab) => pages.TryGetValue(tab, out var page) ? page : null;
    }
}
