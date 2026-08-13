using System;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using canevents.src.Network;
using canevents.src.Tournaments;

namespace canevents.src.Gui
{
    // Debug tab: shortcuts for testing solo - fill a match or a bracket with bots, call a stuck
    // pair by hand. Bot lobbies are excluded from Elo and standings by the result recorder, and
    // bot tournament entrants pay no fee and take no prize.
    public partial class AdminPanelDialog
    {
        private string dbgEventCode = "";
        private int dbgBotCount = 3;

        // Index into BotModeLabels, and the value of BotMatchMode - Play first, because playing the
        // mode is the usual reason to press the button.
        private int dbgBotMode;

        private static readonly string[] BotModeLabels = { "Play", "Watch", "Leave running" };
        private static readonly string[] BotModeButtons =
            { "Start now with bots", "Start and watch", "Start and leave it running" };
        private long dbgTournamentId;
        private int dbgTournamentBots = 7;

        private void DrawDebugTab()
        {
            ImGui.TextColored(ColWarn, "Testing tools. Bot matches never touch Elo or the leaderboard.");
            ImGui.Separator();

            DrawDebugMatches();
            ImGui.Separator();
            DrawDebugTournaments();
            ImGui.Separator();
            DrawDebugState();
        }

        // Matches

        private void DrawDebugMatches()
        {
            Heading("Try an event against bots");

            var events = networkHandler.AdminEventList.Value?.Events;
            if (events == null || events.Length == 0)
            {
                ImGui.TextDisabled("Event list not loaded.");
                if (ImGui.SmallButton("Load")) networkHandler.RequestAdminEventList();
                return;
            }

            var codes = events.Select(e => e.Code).ToArray();
            int index = Math.Max(0, Array.IndexOf(codes, dbgEventCode));
            ImGui.SetNextItemWidth(220);
            if (ImGui.Combo("Event##dbg", ref index, codes, codes.Length)) dbgEventCode = codes[index];
            if (string.IsNullOrEmpty(dbgEventCode)) dbgEventCode = codes[index];

            IntField("Bots", ref dbgBotCount, 1, 32);
            HelpMarker((dbgBotMode == 0 ? "You plus this many bot sides. " : "This many bot sides, without you. ")
                + "Enough of them to satisfy the mode's minimum "
                + "or it will end as soon as it starts.\n\n"
                + "The match runs on a throwaway copy of the event: bots are grown into it, and rewards, "
                + "the entry fee and rating are all off. The event's own config is untouched.");

            ImGui.SetNextItemWidth(220);
            ImGui.Combo("Your part##dbgmode", ref dbgBotMode, BotModeLabels, BotModeLabels.Length);
            HelpMarker("Play: you are in the line-up, fighting the bots.\n\n"
                + "Watch: bots only, and you are seated as an observer as soon as it has an arena - "
                + "the quick way to look at the spectator HUD and the broadcast camera.\n\n"
                + "Leave running: bots only and you are not touched at all. Use this to test joining "
                + "the ordinary way afterwards, from the browser or /canspectate.\n\n"
                + "The last two need at least two bots: with you out of the line-up, they are the "
                + "only sides there are.");

            if (GoButton(BotModeButtons[dbgBotMode]))
            {
                networkHandler.SendAdminAction(AdminAction.BotMatch, dbgEventCode, dbgBotCount,
                    (BotMatchMode)dbgBotMode);
                LogAction(true, "> bot match: " + dbgEventCode + " x" + dbgBotCount
                    + " (" + BotModeLabels[dbgBotMode].ToLowerInvariant() + ")");
            }

            ImGui.SameLine();
            if (ImGui.Button("Force start from queue"))
            {
                networkHandler.SendAdminAction(AdminAction.AnnounceEvent, dbgEventCode);
                LogAction(true, "> force start: " + dbgEventCode);
            }

            ImGui.SameLine();
            if (StopButton("Stop all matches"))
            {
                networkHandler.SendAdminAction(AdminAction.StopAll);
                LogAction(true, "> stop all matches");
            }
        }

        // Tournaments

        private void DrawDebugTournaments()
        {
            Heading("Tournaments");

            var list = networkHandler.TournamentList.Value;
            if (list == null || list.Count == 0)
            {
                ImGui.TextDisabled("No tournaments. Create one on the Tournaments tab.");
                if (ImGui.SmallButton("Refresh##dbgtour")) networkHandler.RequestTournamentList();
                return;
            }

            var labels = new string[list.Count];
            for (int i = 0; i < list.Count; i++)
                labels[i] = "#" + list.Ids[i] + " " + list.NameAt(i) + " [" + StateLabel(list.StateAt(i)) + "]";

            int index = 0;
            for (int i = 0; i < list.Count; i++) if (list.Ids[i] == dbgTournamentId) index = i;

            ImGui.SetNextItemWidth(300);
            if (ImGui.Combo("Tournament##dbg", ref index, labels, labels.Length)) dbgTournamentId = list.Ids[index];
            if (dbgTournamentId <= 0) dbgTournamentId = list.Ids[index];

            var state = (TournamentState)list.StateAt(index);

            IntField("Bot entrants", ref dbgTournamentBots, 1, 64);

            bool canAddBots = state == TournamentState.Draft || state == TournamentState.Signup
                           || state == TournamentState.Seeded;
            ImGui.BeginDisabled(!canAddBots);
            if (GoButton("Fill with bots"))
            {
                SendTourDebug("bots", dbgTournamentBots);
                LogAction(true, "> tournament #" + dbgTournamentId + ": +" + dbgTournamentBots + " bots");
            }
            ImGui.EndDisabled();
            if (!canAddBots) { ImGui.SameLine(); ImGui.TextDisabled("(only before the bracket starts)"); }
            HelpMarker("Bots cannot pay an entry fee, so a tournament with one configured will refuse them.");

            ImGui.BeginDisabled(state != TournamentState.Running);
            if (ImGui.Button("Call next match now"))
            {
                SendTourDebug("forcecall", 0);
                LogAction(true, "> tournament #" + dbgTournamentId + ": force-call");
            }
            ImGui.EndDisabled();
            HelpMarker("Ignores the parallel-match limit. If nothing can be called it says why - "
                + "someone offline, no free arena.");

            ImGui.SameLine();
            if (ImGui.Button("Refresh##dbgtour2"))
            {
                networkHandler.RequestTournamentList();
                networkHandler.RequestTournamentBracket(dbgTournamentId);
            }
        }

        private void SendTourDebug(string action, int intArg)
        {
            networkHandler.SendTournamentAdminAction(new TournamentAdminActionMsg
            {
                Action = action,
                TournamentId = dbgTournamentId,
                MatchId = intArg
            });
            networkHandler.RequestTournamentList();
        }

        // Server state

        private void DrawDebugState()
        {
            Heading("Server state");

            var s = networkHandler.ScheduleState.Value;
            if (s == null)
            {
                ImGui.TextDisabled("Schedule state not loaded.");
                if (ImGui.SmallButton("Load##dbgstate")) networkHandler.RequestScheduleState();
                return;
            }

            ImGui.Text(string.Format("Running matches: {0} / {1}", s.RunningEvents, s.MaxConcurrentEvents));
            ImGui.Text(string.Format("Auto-scheduler: {0}, every {1} min", s.Enabled ? "on" : "off", s.IntervalMinutes));
            ImGui.TextDisabled("A running tournament pauses the auto-scheduler when configured to.");

            if (ImGui.SmallButton(s.Enabled ? "Pause scheduler" : "Resume scheduler"))
            {
                networkHandler.SendAdminAction(s.Enabled ? AdminAction.ScheduleOff : AdminAction.ScheduleOn);
                networkHandler.RequestScheduleState();
            }
        }
    }
}
