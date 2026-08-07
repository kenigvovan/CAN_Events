using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers;
using canevents.src.EventManagers.events;

namespace canevents.src.QueueManagers
{
    /// <summary>
    /// Collects solo players into as many teams as possible (MaxPartiesCount down to
    /// MinPartiesCount, MaxPartySize per team). Stateless between ticks.
    /// </summary>
    public class ByPartsPartyCollectedQueueManager : QueueManager
    {
        private static readonly Random _rng = new Random();

        public override IReadOnlyList<TeamAssembly> TryAssemble(CANEvent canEvent, IReadOnlyList<Registration> pending)
        {
            var eb          = canEvent.AssemblingEB;
            int maxTeamSize = Math.Max(1, eb.MaxPartySize);
            int minTeamSize = Math.Max(1, eb.MinPartySize);
            int minTeams    = Math.Max(1, eb.MinPartiesCount);
            int maxTeams    = Math.Max(minTeams, eb.MaxPartiesCount);

            var available = pending
                .Where(r => !r.MarkedUsed && r.PlayersGuids.Count == 1 && !AnyPlayerBusy(r))
                .ToList();

            var completed = new List<TeamAssembly>();

            while (true)
            {
                // Pick the most teams we can fill (MaxPartiesCount down to MinPartiesCount).
                int teamsCount = 0;
                int teamSize   = 0;
                for (int tc = maxTeams; tc >= minTeams; tc--)
                {
                    int ts = Math.Min(maxTeamSize, available.Count / tc);
                    if (ts >= minTeamSize) { teamsCount = tc; teamSize = ts; break; }
                }
                if (teamsCount == 0) break;

                int needed = teamSize * teamsCount;
                var picked = available.OrderBy(_ => _rng.Next()).Take(needed).ToList();

                var assembly = new TeamAssembly();
                for (int t = 0; t < teamsCount; t++)
                {
                    assembly.AddTeam(picked.GetRange(t * teamSize, teamSize));
                }

                foreach (var r in picked)
                {
                    r.MarkedUsed = true;
                    available.Remove(r);
                }
                assembly.Completed = true;
                completed.Add(assembly);
            }

            return completed;
        }

        public override (bool ok, string message) CheckRegistrationIsSuitable(Registration reg)
            => PartySizeValidator.SoloOnly(reg);

        // Assemblies are formed complete in one call: no partial cross-tick state to clean up.
    }
}
