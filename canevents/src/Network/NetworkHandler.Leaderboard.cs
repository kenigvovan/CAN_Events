using System.Linq;
using Vintagestory.API.Server;

namespace canevents.src.Network
{
    // Server-side read-only stats push: match history and the aggregate leaderboard (optionally
    // filtered by event code).
    public partial class ServerNetworkHandler
    {
        private void OnRequestMatchHistory(IServerPlayer player, RequestMatchHistoryMsg msg)
        {
            if (MatchHistory == null) return;
            var entries = MatchHistory.Entries;
            var result = new MatchHistoryEntryMsg[entries.Count];
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                var scores = new MatchHistoryScoreMsg[e.Scores.Count];
                for (int j = 0; j < e.Scores.Count; j++)
                    scores[j] = new MatchHistoryScoreMsg { PlayerName = e.Scores[j].PlayerName, Kills = e.Scores[j].Kills, Deaths = e.Scores[j].Deaths, Won = e.Scores[j].Won };
                result[i] = new MatchHistoryEntryMsg { EventCode = e.EventCode, TimestampUnix = e.TimestampUnix, WinnerNames = e.WinnerNames, Scores = scores, DurationSeconds = e.DurationSeconds };
            }
            serverChannel.SendPacket(new MatchHistoryMsg { Entries = result }, player);
        }

        private void OnLeaderboardRequest(IServerPlayer player, RequestLeaderboardMsg msg)
        {
            var leaderboard = eventManager.Leaderboard;
            if (leaderboard == null) return;

            var allStats = leaderboard.GetAllStats(string.IsNullOrEmpty(msg.EventCode) ? null : msg.EventCode);
            var entries = new System.Collections.Generic.List<LeaderboardEntryMsg>();

            foreach (var kvp in allStats)
            {
                var playerData = sapi.PlayerData.GetPlayerDataByUid(kvp.Key);
                string name = playerData?.LastKnownPlayername ?? kvp.Key;
                var s = kvp.Value;
                entries.Add(new LeaderboardEntryMsg
                {
                    PlayerName = name,
                    Wins = s.Wins,
                    Losses = s.Losses,
                    Kills = s.Kills,
                    Deaths = s.Deaths,
                    EventsPlayed = s.EventsPlayed,
                    KDRatio = s.KDRatio,
                    WinRate = s.WinRate,
                    Rating = s.Rating
                });
            }

            // A specific event is the rated ladder → sort by MMR; the combined view has no
            // meaningful rating, so it ranks by wins.
            if (!string.IsNullOrEmpty(msg.EventCode))
                entries.Sort((a, b) => b.Rating.CompareTo(a.Rating));
            else
                entries.Sort((a, b) => b.Wins.CompareTo(a.Wins));

            serverChannel.SendPacket(new LeaderboardMsg
            {
                Entries = entries.ToArray(),
                AvailableEventCodes = leaderboard.GetEventCodes().ToArray(),
                FilteredEventCode = msg.EventCode
            }, player);
        }
    }
}
