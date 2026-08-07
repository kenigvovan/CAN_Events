using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers;
using canevents.src.EventManagers.events;

namespace canevents.src.QueueManagers
{
    /// <summary>
    /// Shared by both ranked matchmakers: rating and sorting waiting sides, and the tolerance
    /// formula. The formula is the matchmaking policy - kept in one place so ranked FFA and ranked
    /// ByParts cannot pair players by different standards while feeding the same Elo table.
    /// Per-manager: which registrations are eligible and how an accepted run becomes teams.
    /// </summary>
    public abstract class RankedMatchmakerBase : QueueManager
    {
        private readonly int baseTolerance;
        private readonly int widenPerSecond;

        protected RankedMatchmakerBase(int baseTolerance, int widenPerSecond)
        {
            this.baseTolerance = Math.Max(0, baseTolerance);
            this.widenPerSecond = Math.Max(0, widenPerSecond);
        }

        /// <summary>One waiting side (a solo or a whole party) with the two numbers matchmaking needs.</summary>
        protected sealed class RatedSide
        {
            public Registration Reg;
            public int Rating;
            public long WaitSeconds;
        }

        /// <summary>
        /// Eligible sides this tick, rated and sorted ascending, so the spread of any contiguous run
        /// is simply last minus first.
        /// </summary>
        protected List<RatedSide> RateAndSort(CANEvent canEvent, IReadOnlyList<Registration> pending,
            Func<Registration, bool> eligible)
        {
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            return pending
                .Where(r => !r.MarkedUsed && !AnyPlayerBusy(r) && eligible(r))
                .Select(r => new RatedSide
                {
                    Reg = r,
                    Rating = GetShardRating(r, canEvent.Code),
                    WaitSeconds = Math.Max(0, (nowMs - r.TimeStampCreated) / 1000)
                })
                .OrderBy(c => c.Rating)
                .ToList();
        }

        /// <summary>
        /// Acceptable rating spread for a side: base + widenPerSecond * waited, so close matches form
        /// fast and the window eventually admits anyone.
        /// </summary>
        protected long ToleranceFor(long waitedSeconds) => baseTolerance + widenPerSecond * waitedSeconds;

        /// <summary>The longest wait inside a run: the most patient member sets its tolerance.</summary>
        protected static long LongestWait(List<RatedSide> sides, int start, int count)
        {
            long waited = 0;
            for (int i = start; i < start + count && i < sides.Count; i++)
                if (sides[i].WaitSeconds > waited) waited = sides[i].WaitSeconds;
            return waited;
        }
    }
}
