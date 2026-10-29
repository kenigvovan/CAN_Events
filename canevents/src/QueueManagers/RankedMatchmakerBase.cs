using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers;
using canevents.src.EventManagers.events;

namespace canevents.src.QueueManagers
{
    // Shared rating/tolerance logic so both ranked queues match by the same rules.
    public abstract class RankedMatchmakerBase : QueueManager
    {
        private readonly int baseTolerance;
        private readonly int widenPerSecond;

        protected RankedMatchmakerBase(int baseTolerance, int widenPerSecond)
        {
            this.baseTolerance = Math.Max(0, baseTolerance);
            this.widenPerSecond = Math.Max(0, widenPerSecond);
        }

        protected sealed class RatedSide
        {
            public Registration Reg;
            public int Rating;
            public long WaitSeconds;
        }

        // Sorted ascending, so a run's spread is last minus first.
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

        protected long ToleranceFor(long waitedSeconds) => baseTolerance + widenPerSecond * waitedSeconds;

        // The longest waiter in a run sets its tolerance.
        protected static long LongestWait(List<RatedSide> sides, int start, int count)
        {
            long waited = 0;
            for (int i = start; i < start + count && i < sides.Count; i++)
                if (sides[i].WaitSeconds > waited) waited = sides[i].WaitSeconds;
            return waited;
        }
    }
}
