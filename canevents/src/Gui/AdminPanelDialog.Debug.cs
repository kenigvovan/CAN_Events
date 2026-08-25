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

        /// <summary>Second click confirms "stop all". One click used to end every live match on the
        /// server, and the button sits right next to the one that starts a test match.</summary>
        private bool dbgStopArmed;

        private void DrawDebugTab()
        {
            // The state line first: every button below is judged against "is anything running".
            DrawDebugState();
            ImGui.Separator();

            DrawDebugMatches();
            ImGui.Separator();
            DrawDebugTournaments();
            ImGui.Separator();
            DrawActionLog();
        }

        // Matches

        private void DrawDebugMatches()
        {
            SectionHeader("Try an event against bots", "dbgevents",
                () => networkHandler.RequestAdminEventList());

            var events = networkHandler.AdminEventList.Value?.Events;
            if (events == null || events.Length == 0)
            {
                EmptyState("Event list not loaded.", "Press Refresh above.");
                return;
            }

            var codes = events.Select(e => e.Code).ToArray();
            int index = Math.Max(0, Array.IndexOf(codes, dbgEventCode));
            ImGui.SetNextItemWidth(220);
            if (ImGui.Combo("Event##dbg", ref index, codes, codes.Length)) dbgEventCode = codes[index];
            if (string.IsNullOrEmpty(dbgEventCode)) dbgEventCode = codes[index];

            // Bots and role on one line: they are read together ("me plus three" / "three, watching").
            IntField("Bots", ref dbgBotCount, 1, 32, width: 80f);
            HelpMarker((dbgBotMode == 0 ? "You plus this many bot sides. " : "This many bot sides, without you. ")
                + "Enough of them to satisfy the mode's minimum "
                + "or it will end as soon as it starts.\n\n"
                + "The match runs on a throwaway copy of the event: bots are grown into it, and rewards, "
                + "the entry fee and rating are all off. The event's own config is untouched.");

            ImGui.SameLine();
            ImGui.SetNextItemWidth(150);
            ImGui.Combo("Your part##dbgmode", ref dbgBotMode, BotModeLabels, BotModeLabels.Length);
            HelpMarker("Play: you are in the line-up, fighting the bots.\n\n"
                + "Watch: bots only, and you are seated as an observer as soon as it has an arena - "
                + "the quick way to look at the spectator HUD and the broadcast camera.\n\n"
                + "Leave running: bots only and you are not touched at all. Use this to test joining "
                + "the ordinary way afterwards, from the browser or /canspectate.\n\n"
                + "The last two need at least two bots: with you out of the line-up, they are the "
                + "only sides there are.");

            // Says what will happen before it happens - the mode changes what the button means.
            ImGui.TextDisabled(dbgBotMode == 0
                ? "You and " + dbgBotCount + " bot(s) on '" + dbgEventCode + "'."
                : dbgBotCount + " bot(s) on '" + dbgEventCode + "', you "
                  + (dbgBotMode == 1 ? "watching." : "not involved."));

            // Watch and Leave-running need two sides; one bot alone ends the moment it starts.
            bool tooFewBots = dbgBotMode != 0 && dbgBotCount < 2;
            ImGui.BeginDisabled(tooFewBots);
            if (GoButton(BotModeButtons[dbgBotMode]))
            {
                networkHandler.SendAdminAction(AdminAction.BotMatch, dbgEventCode, dbgBotCount,
                    (BotMatchMode)dbgBotMode);
                LogAction(true, "> bot match: " + dbgEventCode + " x" + dbgBotCount
                    + " (" + BotModeLabels[dbgBotMode].ToLowerInvariant() + ")");
            }
            ImGui.EndDisabled();
            if (tooFewBots)
            {
                ImGui.SameLine();
                ImGui.TextColored(ColWarn, "needs at least 2 bots without you");
            }

            ImGui.SameLine();
            if (ImGui.Button("Force start from queue"))
            {
                networkHandler.SendAdminAction(AdminAction.AnnounceEvent, dbgEventCode);
                LogAction(true, "> force start: " + dbgEventCode);
            }
            HelpMarker("Starts this event from whoever is queued for it right now, ignoring the "
                + "minimum. Nothing to do with bots.");
        }

        // Tournaments

        private void DrawDebugTournaments()
        {
            SectionHeader("Tournaments", "dbgtour", () =>
            {
                networkHandler.RequestTournamentList();
                if (dbgTournamentId > 0) networkHandler.RequestTournamentBracket(dbgTournamentId);
            });

            var list = networkHandler.TournamentList.Value;
            if (list == null || list.Count == 0)
            {
                EmptyState("No tournaments yet.", "Create one on the Tournaments tab.");
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

            IntField("Bot entrants", ref dbgTournamentBots, 1, 64, width: 80f);
            HelpMarker("Bots cannot pay an entry fee, so a tournament with one configured will refuse them.");

            // Both actions on one line, each greyed out when this tournament's state forbids it -
            // and the reason spelled out next to it, instead of a button that silently does nothing.
            bool canAddBots = state == TournamentState.Draft || state == TournamentState.Signup
                           || state == TournamentState.Seeded;
            ImGui.SameLine();
            ImGui.BeginDisabled(!canAddBots);
            if (GoButton("Fill with bots"))
            {
                SendTourDebug("bots", dbgTournamentBots);
                LogAction(true, "> tournament #" + dbgTournamentId + ": +" + dbgTournamentBots + " bots");
            }
            ImGui.EndDisabled();

            ImGui.SameLine();
            ImGui.BeginDisabled(state != TournamentState.Running);
            if (ImGui.Button("Call next match now"))
            {
                SendTourDebug("forcecall", 0);
                LogAction(true, "> tournament #" + dbgTournamentId + ": force-call");
            }
            ImGui.EndDisabled();
            HelpMarker("Ignores the parallel-match limit. If nothing can be called it says why - "
                + "someone offline, no free arena.");

            if (!canAddBots)
                ImGui.TextDisabled("Bots can only be added before the bracket starts.");
            if (state != TournamentState.Running)
                ImGui.TextDisabled("Matches can only be called while the bracket is running.");
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
            SectionHeader("Server state", "dbgstate", () => networkHandler.RequestScheduleState(),
                "Testing tools. Bot matches never touch Elo or the leaderboard.");

            var s = networkHandler.ScheduleState.Value;
            if (s == null)
            {
                EmptyState("Schedule state not loaded.", "Press Refresh above.");
                return;
            }

            // Live count in colour: at a glance, is the server busy or idle?
            ImGui.Text("Running matches:");
            ImGui.SameLine();
            ImGui.TextColored(s.RunningEvents > 0 ? ColOk : ColMuted,
                s.RunningEvents + " / " + s.MaxConcurrentEvents);

            ImGui.SameLine();
            ImGui.TextDisabled("   |   Auto-scheduler:");
            ImGui.SameLine();
            ImGui.TextColored(s.Enabled ? ColOk : ColMuted,
                s.Enabled ? "on, every " + s.IntervalMinutes + " min" : "off");

            if (ImGui.SmallButton(s.Enabled ? "Pause scheduler" : "Resume scheduler"))
            {
                networkHandler.SendAdminAction(s.Enabled ? AdminAction.ScheduleOff : AdminAction.ScheduleOn);
                networkHandler.RequestScheduleState();
                LogAction(true, s.Enabled ? "> scheduler paused" : "> scheduler resumed");
            }
            ImGui.SameLine();
            HelpMarkerInline("A running tournament pauses the auto-scheduler when configured to.");

            // Nothing to stop, nothing to arm: the button is only dangerous while matches are live.
            ImGui.SameLine();
            ImGui.BeginDisabled(s.RunningEvents == 0);
            if (ArmedButton(ref dbgStopArmed, "Stop all matches",
                "Really stop " + s.RunningEvents + "?"))
            {
                networkHandler.SendAdminAction(AdminAction.StopAll);
                networkHandler.RequestScheduleState();
                LogAction(true, "> stop all matches");
            }
            ImGui.EndDisabled();
        }
    }
}
