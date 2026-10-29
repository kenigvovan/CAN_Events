using System;
using System.Collections.Generic;
using System.Linq;

namespace canevents.src.EventManagers.eventBehavior.Shared
{
    // Elimination order for last-one-standing modes. Disconnects never hit the death path,
    // so they are not recorded and rank last.
    public sealed class SurvivalRanking
    {
        // First out first.
        private readonly List<string> order = new List<string>();

        public void Clear() => order.Clear();

        public void Add(string uid)
        {
            if (uid == null || order.Contains(uid)) return;
            order.Add(uid);
        }

        // Still in play = MaxValue, never recorded = -1.
        public int SurvivalOf(RunningEvent runningEvent, string uid)
            => runningEvent.IsActive(uid) ? int.MaxValue : order.IndexOf(uid);

        // A party lasts as long as its last member.
        public List<HashSet<string>> Rank(RunningEvent runningEvent, Func<string, int> tieBreak = null)
        {
            var groups = runningEvent.ParticipantsByGroupsBefore;
            var winners = runningEvent.GetWinners();
            Func<string, int> tb = tieBreak ?? (_ => 0);

            if (groups != null && groups.Any(g => g.Count > 1))
            {
                return groups
                    .OrderByDescending(g => g.Any(uid => winners.Contains(uid)))
                    .ThenByDescending(g => g.Max(uid => SurvivalOf(runningEvent, uid)))
                    .ThenByDescending(g => g.Sum(uid => tb(uid)))
                    .Select(g => new HashSet<string>(g))
                    .ToList();
            }

            // Winner flag first (time-up winners). Tie-break negated: third key sorts ascending.
            return ScoringUtil.RankIndividually(runningEvent.ParticipantsBefore,
                uid => winners.Contains(uid) ? 1 : 0,
                uid => SurvivalOf(runningEvent, uid),
                uid => -tb(uid));
        }
    }
}
