using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers;
using canevents.src.EventManagers.events;

namespace canevents.src.QueueManagers
{
    /// <summary>
    /// Ranked counterpart of <see cref="ByPartsPartyCollectedQueueManager"/>: picks the match from a
    /// contiguous run of the rating-sorted queue (accepted only within the tolerance its longest
    /// waiter has earned, see <see cref="RankedMatchmakerBase.ToleranceFor"/>), then splits it by
    /// snake draft so team totals stay close - random assignment would still stack one side.
    /// </summary>
    public class RankedByPartsQueueManager : RankedMatchmakerBase
    {
        public RankedByPartsQueueManager(int baseTolerance, int widenPerSecond)
            : base(baseTolerance, widenPerSecond) { }

        public override IReadOnlyList<TeamAssembly> TryAssemble(CANEvent canEvent, IReadOnlyList<Registration> pending)
        {
            var eb = canEvent.AssemblingEB;
            if (eb == null) return Array.Empty<TeamAssembly>();

            int maxTeamSize = Math.Max(1, eb.MaxPartySize);
            int minTeamSize = Math.Max(1, eb.MinPartySize);
            int minTeams = Math.Max(1, eb.MinPartiesCount);
            int maxTeams = Math.Max(minTeams, eb.MaxPartiesCount);
            int fillSeconds = Math.Max(0, eb.QueueFillSeconds);

            // Solo registrations only, sorted by rating so similar players sit next to each other.
            var available = RateAndSort(canEvent, pending, r => r.PlayersGuids.Count == 1);

            var completed = new List<TeamAssembly>();

            while (true)
            {
                // Prefer the biggest lobby but fall back to fewer teams: a run tight enough for 2
                // teams often exists when one spanning 4 does not.
                int teamsCount = 0, teamSize = 0, start = -1;
                for (int tc = maxTeams; tc >= minTeams; tc--)
                {
                    int ts = Math.Min(maxTeamSize, available.Count / tc);
                    if (ts < minTeamSize) continue;

                    int candidate = FindAcceptableRun(available, ts * tc);
                    if (candidate < 0) continue;

                    // Smaller-than-max lobbies must sit out the fill window, else the first two solos in
                    // a min-2/max-4 event match instantly and the larger lobby never forms.
                    if (tc < maxTeams && fillSeconds > 0
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

        /// <summary>
        /// Index of the tightest run of consecutive candidates whose rating spread is within the
        /// tolerance its longest-waiting member has earned, or -1.
        /// </summary>
        private int FindAcceptableRun(List<RatedSide> sorted, int needed)
        {
            int best = -1;
            int bestSpread = int.MaxValue;

            for (int i = 0; i + needed <= sorted.Count; i++)
            {
                int spread = sorted[i + needed - 1].Rating - sorted[i].Rating;

                // The most patient player in the run sets how far it may stretch.
                if (spread > ToleranceFor(LongestWait(sorted, i, needed))) continue;

                if (spread < bestSpread) { bestSpread = spread; best = i; }
            }

            return best;
        }

        /// <summary>
        /// Snake draft: best-first, teams take players in the order A,B,C,C,B,A,... so the strongest
        /// and weakest ends even out instead of stacking.
        /// </summary>
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

        // Assemblies are formed complete in one call: no partial state to clean up.
    }
}
