using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers;
using canevents.src.EventManagers.events;
using canevents.src.EventManagers.eventBehavior.abstracts;

namespace canevents.src.QueueManagers
{
    /// <summary>
    /// Skill-based matchmaker for Rated events: each registration is one side (solo or whole party)
    /// rated by its members' average Elo, and matches are carved out of the closest-rated runs of the
    /// sorted queue, within the tolerance the oldest side has earned
    /// (see <see cref="RankedMatchmakerBase.ToleranceFor"/>). Full lobbies start on a tight spread;
    /// smaller ones also need the oldest side to have waited QueueFillSeconds.
    /// Keeps NO partial state between ticks, so a better-matched side arriving later is still used.
    /// </summary>
    public class RankedQueueManager : RankedMatchmakerBase
    {
        public RankedQueueManager(int baseTolerance, int widenPerSecond)
            : base(baseTolerance, widenPerSecond) { }

        public override (bool ok, string message) CheckRegistrationIsSuitable(Registration reg)
            => PartySizeValidator.Check(reg);

        public override IReadOnlyList<TeamAssembly> TryAssemble(CANEvent canEvent, IReadOnlyList<Registration> pending)
        {
            var eb = canEvent.AssemblingEB;
            if (eb == null) return Array.Empty<TeamAssembly>();

            // A side is one registration as it stands: a solo or a whole party of a legal size.
            var cands = RateAndSort(canEvent, pending,
                r => r.PlayersCount >= eb.MinPartySize && r.PlayersCount <= eb.MaxPartySize);

            var result = new List<TeamAssembly>();

            int i = 0;
            while (i + eb.MinPartiesCount <= cands.Count)
            {
                int size = TryFormRunAt(cands, i, eb);
                if (size > 0)
                {
                    var ta = new TeamAssembly { Completed = true };
                    for (int k = i; k < i + size; k++)
                    {
                        ta.AddShard(new TeamShard(cands[k].Reg));
                        cands[k].Reg.MarkedUsed = true;
                    }
                    result.Add(ta);
                    i += size;
                }
                else
                {
                    i++;
                }
            }

            return result;
        }

        // Largest side-count in [MinPartiesCount, MaxPartiesCount] starting at index i that is both
        // within the (wait-widened) rating tolerance and "ready" to start; 0 if none qualifies.
        private int TryFormRunAt(List<RatedSide> cands, int i, PartyAssemblingEB eb)
        {
            for (int size = eb.MaxPartiesCount; size >= eb.MinPartiesCount; size--)
            {
                if (i + size > cands.Count) continue;

                // cands is rating-sorted, so the spread of a contiguous run is last - first.
                int spread = cands[i + size - 1].Rating - cands[i].Rating;

                long oldestWait = LongestWait(cands, i, size);
                long tolerance = ToleranceFor(oldestWait);

                // Full lobbies start as soon as they're close enough; partial lobbies additionally
                // wait out the mode's fill timer (QueueFillSeconds == 0 → no extra wait).
                bool ready = size == eb.MaxPartiesCount || oldestWait >= eb.QueueFillSeconds;

                if (spread <= tolerance && ready) return size;
            }
            return 0;
        }
    }
}
