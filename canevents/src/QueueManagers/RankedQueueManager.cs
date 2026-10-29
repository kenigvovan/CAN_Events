using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers;
using canevents.src.EventManagers.events;
using canevents.src.EventManagers.eventBehavior.abstracts;

namespace canevents.src.QueueManagers
{
    // Ranked matchmaker: each registration is a side rated by average Elo. Stateless between ticks.
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

        // Largest run size at i within tolerance and ready to start, or 0.
        private int TryFormRunAt(List<RatedSide> cands, int i, PartyAssemblingEB eb)
        {
            for (int size = eb.MaxPartiesCount; size >= eb.MinPartiesCount; size--)
            {
                if (i + size > cands.Count) continue;

                int spread = cands[i + size - 1].Rating - cands[i].Rating;

                long oldestWait = LongestWait(cands, i, size);
                long tolerance = ToleranceFor(oldestWait);

                // Partial lobbies also wait QueueFillSeconds.
                bool ready = size == eb.MaxPartiesCount || oldestWait >= eb.QueueFillSeconds;

                if (spread <= tolerance && ready) return size;
            }
            return 0;
        }
    }
}
