using System;
using System.Collections.Generic;
using System.Linq;

namespace canevents.src.EventManagers.eventBehavior.Shared
{
    /// <summary>
    /// Who went out, in what order, and the placement that implies: last one standing first, then the
    /// eliminated in reverse order of leaving, then anyone who left without being eliminated (a
    /// disconnect never passes through the death path). The roster knows who is in play but not the
    /// order they left in, so last-one-standing modes share this instead of each tracking it.
    /// </summary>
    public sealed class SurvivalRanking
    {
        private readonly List<string> order = new List<string>();

        /// <summary>Elimination order, first out first.</summary>
        public IReadOnlyList<string> Order => order;

        public int Count => order.Count;

        /// <summary>Forget the previous match. Call where the mode resets its scores.</summary>
        public void Clear() => order.Clear();

        /// <summary>Records a player as out. Ignores a repeat: the first exit is the one that counts.</summary>
        public void Add(string uid)
        {
            if (uid == null || order.Contains(uid)) return;
            order.Add(uid);
        }

        /// <summary>
        /// How long this player lasted, as a sortable number: still in play beats every exit, a later
        /// exit beats an earlier one, never-recorded (left without dying) ranks below all of them.
        /// </summary>
        public int SurvivalOf(RunningEvent runningEvent, string uid)
            => runningEvent.IsActive(uid) ? int.MaxValue : order.IndexOf(uid);

        /// <summary>
        /// Final placement. Solo lobbies rank player by player; with real parties each party is one
        /// bracket and lives until its last member falls. <paramref name="tieBreak"/> separates equal
        /// survival (kills in Hunger Games); modes without one pass null.
        /// </summary>
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

            // Winner flag first: a mode can declare a winner the survival list alone would not single
            // out (a time-up call). Kills are negated into the ascending third key.
            return ScoringUtil.RankIndividually(runningEvent.ParticipantsBefore,
                uid => winners.Contains(uid) ? 1 : 0,
                uid => SurvivalOf(runningEvent, uid),
                uid => -tb(uid));
        }
    }
}
