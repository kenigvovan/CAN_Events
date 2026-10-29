using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers;
using canevents.src.EventManagers.events;

namespace canevents.src.QueueManagers
{
    // Random solo teams, as many as fit. Stateless between ticks.
    public class ByPartsPartyCollectedQueueManager : QueueManager
    {
        private static readonly Random _rng = new Random();

        public override IReadOnlyList<TeamAssembly> TryAssemble(CANEvent canEvent, IReadOnlyList<Registration> pending)
        {
            var eb = canEvent.AssemblingEB;

            var available = pending
                .Where(r => !r.MarkedUsed && r.PlayersGuids.Count == 1 && !AnyPlayerBusy(r))
                .ToList();

            var completed = new List<TeamAssembly>();

            while (true)
            {
                var (teamsCount, teamSize, _) = SoloLobbyShapes(eb, available.Count).FirstOrDefault();
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
    }
}
