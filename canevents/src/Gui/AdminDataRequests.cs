using canevents.src.Network;

namespace canevents.src.Gui
{
    public static class AdminDataRequests
    {
        public static void RequestDataFor(ClientNetworkHandler networkHandler, PanelTab tab)
        {
            switch (tab)
            {
                case PanelTab.Dashboard: networkHandler.RequestScheduleState(); networkHandler.RequestAdminEventList(); break;
                // Schedule state has the open signup windows.
                case PanelTab.Events:
                    networkHandler.RequestAdminEventList();
                    networkHandler.RequestPlaceList();
                    networkHandler.RequestScheduleState();
                    break;
                case PanelTab.Arenas: networkHandler.RequestPlaceList(); break;
                case PanelTab.NewArena: networkHandler.RequestAdminEventList(); networkHandler.RequestPlaceList(); break;
                case PanelTab.Content: networkHandler.RequestLoadoutList(); break;
                case PanelTab.Economy: networkHandler.RequestAdminEventList(); break;
                case PanelTab.Tournaments:
                    networkHandler.RequestTournamentList();
                    networkHandler.RequestAdminEventList();
                    networkHandler.RequestPlaceList();
                    // Only the player list carries loadout names.
                    networkHandler.RequestEventList();
                    break;
                case PanelTab.Debug:
                    networkHandler.RequestAdminEventList();
                    networkHandler.RequestTournamentList();
                    networkHandler.RequestScheduleState();
                    break;
                case PanelTab.Players: networkHandler.RequestPlayerAccess(); break;
            }
        }
    }
}
