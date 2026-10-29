using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace canevents.src.commands
{
    // Player commands: queue, spectating, duels, stats.
    public partial class CommandsImplementations
    {
        public TextCommandResult RegPlayer(TextCommandCallingArgs args)
        {
            IServerPlayer player = args.Caller.Player as IServerPlayer;

            string eventCode = null;
            if (args.Parsers != null && args.Parsers.Count > 0)
            {
                eventCode = (string)args.Parsers[0].GetValue();
            }

            return Wrap(canEventManager.Queueing.Join(player, eventCode));
        }

        public TextCommandResult LeaveQueue(TextCommandCallingArgs args)
        {
            IServerPlayer player = args.Caller.Player as IServerPlayer;

            // Leaves every queue.
            return Wrap(canEventManager.Queueing.Leave(player));
        }

        // Escape hatch for players stuck in spectator after a crash.
        public TextCommandResult LeaveSpectator(TextCommandCallingArgs args)
        {
            var player = args.Caller.Player as IServerPlayer;
            if (player == null) return TextCommandResult.Error("Players only.");
            if (canEventManager == null) return TextCommandResult.Error("Event manager not available.");

            var (ok, message) = canEventManager.LeaveSpectating(player);
            return ok ? TextCommandResult.Success(message) : TextCommandResult.Error(message);
        }

        public TextCommandResult ToggleQuiet(TextCommandCallingArgs args)
        {
            var player = args.Caller.Player as IServerPlayer;
            if (player == null) return TextCommandResult.Error("Players only.");

            string mode = args.Parsers[0].GetValue() as string;
            bool quiet = mode == "on" || (mode != "off" && !AnnouncementPrefs.IsQuiet(player));
            AnnouncementPrefs.SetQuiet(player, quiet);
            return TextCommandResult.Success(Lang.Get(quiet ? "canevents:quiet-on" : "canevents:quiet-off"));
        }

        public TextCommandResult WatchEvent(TextCommandCallingArgs args)
        {
            var player = args.Caller.Player as IServerPlayer;
            if (player == null) return TextCommandResult.Error("Players only.");
            if (canEventManager == null) return TextCommandResult.Error("Event manager not available.");

            // Omitted arg is null; 0 means "the only running match".
            var rawId = args.Parsers[0].GetValue();
            long eventId = rawId == null ? 0 : Convert.ToInt64(rawId);

            var (ok, message) = canEventManager.StartObserving(player, eventId);
            if (ok) return TextCommandResult.Success(message);

            var live = canEventManager.WatchableEvents();
            if (eventId <= 0 && live.Count > 1)
                return TextCommandResult.Success(message + "\n" + DescribeWatchable(live));

            return TextCommandResult.Error(message);
        }

        // A command because the spectator panel is a HUD and never takes the cursor.
        public TextCommandResult FollowParticipant(TextCommandCallingArgs args)
        {
            var player = args.Caller.Player as IServerPlayer;
            if (player == null) return TextCommandResult.Error("Players only.");

            string name = args.Parsers[0].GetValue() as string;
            if (string.IsNullOrWhiteSpace(name))
                return TextCommandResult.Error(Lang.Get("canevents:spectate-follow-nobody"));

            var net = sapi.ModLoader.GetModSystem<canevents>()?.ServerNetworkHandler;
            if (net == null) return TextCommandResult.Error("Network handler not available.");

            // By name: bots have no player object for PlayerByUid.
            string uid = ResolveParticipantUid(player, name);
            if (uid == null) return TextCommandResult.Error(Lang.Get("canevents:spectate-follow-no-such", name));

            var (ok, message) = net.FollowParticipant(player, uid);
            return ok ? TextCommandResult.Success(message) : TextCommandResult.Error(message);
        }

        private string ResolveParticipantUid(IServerPlayer observer, string name)
        {
            long eventId = canEventManager?.SpectatorManager?.SeatedEventId(observer.PlayerUID) ?? 0;
            var re = eventId > 0 ? canEventManager.FindRunningEvent(eventId) : null;
            if (re == null) return null;

            foreach (var uid in re.ParticipantsBefore)
                if (string.Equals(canEventManager.NameOf(uid), name, StringComparison.OrdinalIgnoreCase))
                    return uid;
            return null;
        }

        public TextCommandResult DuelChallenge(TextCommandCallingArgs args)
        {
            var player = args.Caller.Player as IServerPlayer;
            if (player == null) return TextCommandResult.Error("Players only.");

            var duels = sapi.ModLoader.GetModSystem<canevents>()?.Duels;
            if (duels == null) return TextCommandResult.Error(Lang.Get("canevents:duel-disabled"));

            string targetName = args.Parsers[0].GetValue() as string;

            // Defaults to friendly. A stake implies a wager: "/canduel bob gear-rusty:2".
            string second = (args.Parsers[1].GetValue() as string)?.Trim();
            bool isWager = !string.IsNullOrEmpty(second)
                && !second.Equals("friendly", StringComparison.OrdinalIgnoreCase);

            string wager = null;
            if (isWager)
                wager = second.Equals("wager", StringComparison.OrdinalIgnoreCase) ? null : second;

            if (isWager && wager == null)
                return TextCommandResult.Error(Lang.Get("canevents:duel-wager-needs-items"));

            var (ok, message) = duels.Challenge(player, targetName,
                isWager ? Duels.DuelKind.Wager : Duels.DuelKind.Friendly, wager);
            return ok ? TextCommandResult.Success(message) : TextCommandResult.Error(message);
        }

        public TextCommandResult DuelAccept(TextCommandCallingArgs args)
        {
            var player = args.Caller.Player as IServerPlayer;
            if (player == null) return TextCommandResult.Error("Players only.");

            var duels = sapi.ModLoader.GetModSystem<canevents>()?.Duels;
            if (duels == null) return TextCommandResult.Error(Lang.Get("canevents:duel-disabled"));

            var (ok, message) = duels.Accept(player);
            return ok ? TextCommandResult.Success(message) : TextCommandResult.Error(message);
        }

        public TextCommandResult DuelDecline(TextCommandCallingArgs args)
        {
            var player = args.Caller.Player as IServerPlayer;
            if (player == null) return TextCommandResult.Error("Players only.");

            var duels = sapi.ModLoader.GetModSystem<canevents>()?.Duels;
            if (duels == null) return TextCommandResult.Error(Lang.Get("canevents:duel-disabled"));

            var (ok, message) = duels.Decline(player);
            return ok ? TextCommandResult.Success(message) : TextCommandResult.Error(message);
        }

        public TextCommandResult ListWatchable(TextCommandCallingArgs args)
        {
            if (canEventManager == null) return TextCommandResult.Error("Event manager not available.");

            var live = canEventManager.WatchableEvents();
            if (live.Count == 0) return TextCommandResult.Success(Lang.Get("canevents:spectate-none-running"));

            return TextCommandResult.Success(DescribeWatchable(live));
        }

        private static string DescribeWatchable(List<RunningEvent> live)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var re in live)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(Lang.Get("canevents:spectate-list-row",
                    re.Id,
                    re.Event?.Code ?? "?",
                    re.Place?.Name ?? "-",
                    re.ParticipantsStillInEvent.Count,
                    re.ParticipantsBefore.Count));
            }
            return sb.ToString();
        }

        public TextCommandResult ShowStats(TextCommandCallingArgs args)
        {
            IServerPlayer player = args.Caller.Player as IServerPlayer;
            var leaderboard = sapi.ModLoader.GetModSystem<canevents>()?.CANEventManager?.Leaderboard;

            if (leaderboard == null)
            {
                return TextCommandResult.Error("Leaderboard not available.");
            }

            var allStats = leaderboard.GetAllStats();
            if (!allStats.TryGetValue(player.PlayerUID, out var stats))
            {
                return TextCommandResult.Success("No stats recorded yet.");
            }
            string msg = string.Format(
                "=== Your Stats ===\nWins: {0} | Losses: {1} | Win rate: {5:F0}%\nKills: {2} | Deaths: {3} | K/D: {6:F2}\nEvents Played: {4}",
                stats.Wins, stats.Losses, stats.Kills, stats.Deaths, stats.EventsPlayed, stats.WinRate, stats.KDRatio);

            // MMR is per event.
            var byEvent = leaderboard.GetPlayerStatsByEvent(player.PlayerUID)
                .Where(kv => kv.Value.EventsPlayed > 0)
                .OrderByDescending(kv => kv.Value.Rating)
                .ToList();
            if (byEvent.Count > 0)
            {
                msg += "\n=== MMR ===";
                foreach (var kv in byEvent)
                    msg += string.Format("\n{0}: {1}", kv.Key, kv.Value.Rating);
            }

            return TextCommandResult.Success(msg);
        }
    }
}
