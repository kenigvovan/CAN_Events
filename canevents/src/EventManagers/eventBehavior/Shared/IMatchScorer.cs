using System.Collections.Generic;

namespace canevents.src.EventManagers.eventBehavior.Shared
{
    public sealed class ScoreboardData
    {
        public Dictionary<string, int> Kills;
        public Dictionary<string, int> Deaths;
        public int[] TeamScores;
        public int ZoneOwnerTeam = -1;
        public int TeamScoreLimit;
    }

    // RunningEvent resolves exactly one scorer per match (RunningEvent.Scorer).
    public interface IMatchScorer
    {
        // Placement brackets, best first; ties share a bracket. Null if no ranking.
        List<HashSet<string>> GetFinalRanking();

        Dictionary<string, int> GetFinalKillCounts() => null;
        Dictionary<string, int> GetFinalDeathCounts() => null;

        // Also sent to spectators who join mid-match.
        ScoreboardData BuildScoreboard() => new ScoreboardData
        {
            Kills = GetFinalKillCounts() ?? new Dictionary<string, int>(),
            Deaths = GetFinalDeathCounts()
        };

        // Hidden-role modes: a kill count would reveal the murderer. Applied in ServerNetworkHandler.ToScoreboardMsg.
        bool HidesIndividualScores => false;

        // Checked by every death announcement path, bot deaths included.
        bool HidesKillerIdentity => false;

        // false: the player gets an empty scoreboard, so the HUD drops the table.
        bool ShowsScoreboardTo(string uid) => true;
    }
}
