using canevents.src.Network;
using Vintagestory.API.Config;

namespace canevents.src.Gui.Admin
{
    // Scheduler actions shared by several tabs, so they all send and log the same way.
    public static class PanelActions
    {
        public static void ForceStart(AdminPanelGui gui, string code)
            => Scheduler(gui, Lang.Get("canevents:admin-events-log-start", code), AdminAction.ForceStart, code);

        // minutes < 0 = server default length, 0 = open until started.
        public static void OpenSignup(AdminPanelGui gui, string code, int minutes)
        {
            string length = minutes < 0 ? Lang.Get("canevents:admin-events-log-default-length") : minutes + "m";
            Scheduler(gui, Lang.Get("canevents:admin-events-log-open", code, length), AdminAction.OpenSignup, code, minutes);
        }

        public static void CancelSignup(AdminPanelGui gui, string code)
            => Scheduler(gui, Lang.Get("canevents:admin-events-log-cancel", code), AdminAction.CancelSignup, code);

        public static void StopAll(AdminPanelGui gui)
            => Scheduler(gui, Lang.Get("canevents:admin-debug-log-stop-all"), AdminAction.StopAll);

        public static void SetSchedule(AdminPanelGui gui, bool on)
            => Scheduler(gui, Lang.Get(on ? "canevents:admin-debug-log-resumed" : "canevents:admin-debug-log-paused"),
                on ? AdminAction.ScheduleOn : AdminAction.ScheduleOff);

        public static void SetScheduleInterval(AdminPanelGui gui, int minutes)
            => Scheduler(gui, Lang.Get("canevents:admin-dash-log-interval", minutes), AdminAction.ScheduleInterval, null, minutes);

        private static void Scheduler(AdminPanelGui gui, string log, AdminAction action, string code = null, int value = 0)
        {
            gui.Act(log, () =>
            {
                gui.Net.SendAdminAction(action, code, value);
                gui.Net.RequestScheduleState();
            });
        }
    }
}
