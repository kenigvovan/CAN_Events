using System;
using System.Collections.Generic;
using System.Linq;

namespace canevents.src.EventManagers.eventBehavior.Shared
{
    /// <summary>
    /// Scoring shapes shared by the modes: "who is ahead, and is it actually a tie" plus the
    /// standard individual ranking.
    /// </summary>
    public static class ScoringUtil
    {
        /// <summary>
        /// The single highest-scoring item. False on an empty input or a tie for first place -
        /// a tie is a draw, not a win for whoever the enumeration reached first.
        /// </summary>
        public static bool TryArgMax<T>(IEnumerable<T> items, Func<T, int> scoreOf, out T best)
        {
            best = default;
            int bestScore = int.MinValue;
            bool tie = false;

            foreach (var item in items)
            {
                int score = scoreOf(item);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = item;
                    tie = false;
                }
                else if (score == bestScore)
                {
                    tie = true;
                }
            }

            if (tie) { best = default; return false; }
            return bestScore != int.MinValue;
        }

        /// <summary>
        /// Free-for-all placement: by <paramref name="primary"/> first, then kills, then fewest
        /// deaths. One player per bracket; modes with real teams rank groups instead.
        /// </summary>
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
