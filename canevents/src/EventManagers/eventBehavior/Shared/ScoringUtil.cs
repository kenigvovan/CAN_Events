using System;
using System.Collections.Generic;
using System.Linq;

namespace canevents.src.EventManagers.eventBehavior.Shared
{
    public static class ScoringUtil
    {
        public static int CountOf(Dictionary<string, int> tally, string uid)
            => uid != null && tally != null && tally.TryGetValue(uid, out var n) ? n : 0;

        // False on empty input or a tie for first.
        public static bool TryArgMax<T>(IEnumerable<T> items, Func<T, int> scoreOf, out T best)
            => TryArgMax<T, int>(items, scoreOf, out best);

        // TScore can be a tuple for multi-key ranking, e.g. (kills, damage).
        public static bool TryArgMax<T, TScore>(IEnumerable<T> items, Func<T, TScore> scoreOf, out T best)
            where TScore : IComparable<TScore>
        {
            best = default;
            TScore bestScore = default;
            bool any = false;
            bool tie = false;

            foreach (var item in items)
            {
                var score = scoreOf(item);
                int cmp = any ? score.CompareTo(bestScore) : 1;
                if (cmp > 0)
                {
                    bestScore = score;
                    best = item;
                    any = true;
                    tie = false;
                }
                else if (cmp == 0)
                {
                    tie = true;
                }
            }

            if (tie) { best = default; return false; }
            return any;
        }

        // -1 on a tie or no teams.
        public static int LeadingTeam(int[] teamScores)
            => TryArgMax(Enumerable.Range(0, teamScores?.Length ?? 0), i => teamScores[i], out int best) ? best : -1;

        public static List<HashSet<string>> RankTeams(IReadOnlyList<HashSet<string>> groups,
            HashSet<string> winners, int[] teamScores)
            => RankTeams(groups, winners, i => teamScores != null && i < teamScores.Length ? teamScores[i] : 0);

        // Teams: winner first (forfeit wins can trail on score), then score. Ties share a bracket.
        public static List<HashSet<string>> RankTeams(IReadOnlyList<HashSet<string>> groups,
            HashSet<string> winners, Func<int, int> scoreOf)
        {
            if (groups == null || groups.Count == 0) return null;
            return Enumerable.Range(0, groups.Count)
                .GroupBy(i => (won: groups[i].Any(winners.Contains), score: scoreOf(i)))
                .OrderByDescending(g => g.Key.won)
                .ThenByDescending(g => g.Key.score)
                .Select(g =>
                {
                    var bracket = new HashSet<string>();
                    foreach (var i in g) bracket.UnionWith(groups[i]);
                    return bracket;
                })
                .ToList();
        }

        public static string ScoreLine(int[] teamScores) => string.Join(" : ", teamScores ?? new int[0]);

        // One player per bracket: primary desc, kills desc, deaths asc.
        public static List<HashSet<string>> RankIndividually(
            IEnumerable<string> uids, Func<string, int> primary, Func<string, int> kills, Func<string, int> deaths)
        {
            return uids
                .OrderByDescending(primary)
                .ThenByDescending(kills)
                .ThenBy(deaths)
                .Select(uid => new HashSet<string> { uid })
                .ToList();
        }
    }
}
