using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers;
using canevents.src.EventManagers.events;

namespace canevents.src.QueueManagers
{
    // Ranked ByParts: picks a contiguous run of the rating-sorted queue, then snake-drafts teams.
    public class RankedByPartsQueueManager : RankedMatchmakerBase
    {
        public RankedByPartsQueueManager(int baseTolerance, int widenPerSecond)
            : base(baseTolerance, widenPerSecond) { }

        public override IReadOnlyList<TeamAssembly> TryAssemble(CANEvent canEvent, IReadOnlyList<Registration> pending)
        {
            var eb = canEvent.AssemblingEB;
            if (eb == null) return Array.Empty<TeamAssembly>();

            int fillSeconds = Math.Max(0, eb.QueueFillSeconds);

            // Solos only.
            var available = RateAndSort(canEvent, pending, r => r.PlayersGuids.Count == 1);

            var completed = new List<TeamAssembly>();

            while (true)
            {
                // Biggest lobby first, then fewer teams.
                int teamsCount = 0, teamSize = 0, start = -1;
                foreach (var (tc, ts, largest) in SoloLobbyShapes(eb, available.Count))
                {
                    int candidate = FindAcceptableRun(available, ts * tc);
                    if (candidate < 0) continue;

                    // Smaller lobbies wait out the fill window so bigger ones get a chance.
                    if (!largest && fillSeconds > 0
                        && LongestWait(available, candidate, ts * tc) < fillSeconds) continue;

                    teamsCount = tc;
                    teamSize = ts;
                    start = candidate;
                    break;
                }
                if (teamsCount == 0 || start < 0) break; // nothing close enough yet, the window widens

                var run = available.GetRange(start, teamSize * teamsCount);
                completed.Add(BuildBalancedAssembly(run, teamsCount, teamSize));

                foreach (var c in run)
                {
                    c.Reg.MarkedUsed = true;
                    available.Remove(c);
                }
            }

            return completed;
        }

        // Start index of the tightest acceptable run, or -1.
        private int FindAcceptableRun(List<RatedSide> sorted, int needed)
        {
            int best = -1;
            int bestSpread = int.MaxValue;

            for (int i = 0; i + needed <= sorted.Count; i++)
            {
                int spread = sorted[i + needed - 1].Rating - sorted[i].Rating;

                if (spread > ToleranceFor(LongestWait(sorted, i, needed))) continue;

                if (spread < bestSpread) { bestSpread = spread; best = i; }
            }

            return best;
        }

        // Snake draft: A,B,C,C,B,A,...
        private static TeamAssembly BuildBalancedAssembly(List<RatedSide> run, int teamsCount, int teamSize)
        {
            var teams = new List<List<Registration>>();
            for (int t = 0; t < teamsCount; t++) teams.Add(new List<Registration>());

            var strongestFirst = run.OrderByDescending(c => c.Rating).ToList();
            for (int i = 0; i < strongestFirst.Count; i++)
            {
                int round = i / teamsCount;
                int slot = i % teamsCount;
                int team = (round % 2 == 0) ? slot : teamsCount - 1 - slot; // reverse every other round
                teams[team].Add(strongestFirst[i].Reg);
            }

            var assembly = new TeamAssembly();
            foreach (var members in teams) assembly.AddTeam(members);
            assembly.Completed = true;
            return assembly;
        }

        public override (bool ok, string message) CheckRegistrationIsSuitable(Registration reg)
            => PartySizeValidator.SoloOnly(reg);
    }
}
