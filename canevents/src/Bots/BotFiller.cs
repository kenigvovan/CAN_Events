using System;
using System.Collections.Generic;
using canevents.src.EventManagers;
using canevents.src.EventManagers.events;
using canevents.src.QueueManagers;

namespace canevents.src.Bots
{
    /// <summary>
    /// Tops a half-empty lobby up with bots just before it becomes a match. They join as ordinary
    /// one-player sides, so roster, spawns, eliminations and ranking treat them as players.
    /// Always solo sides: filling a team mode would mean deciding which human gets the bot partner.
    /// </summary>
    public static class BotFiller
    {
        public static int Fill(CANEvent canEvent, TeamAssembly assembly)
        {
            var assembling = canEvent?.AssemblingEB;
            if (assembling == null || assembly == null) return 0;

            int fillTo = Math.Min(assembling.BotFillToParties, assembling.MaxPartiesCount);
            if (fillTo <= 0 || assembly.Shards.Count >= fillTo) return 0;

            int added = 0;
            while (assembly.Shards.Count < fillTo)
            {
                var registration = new Registration(canEvent, new List<string> { BotIdentity.NewUid(added) })
                {
                    // Nothing must try to remove this from the queue store: it was never in it.
                    MarkedUsed = true
                };
                assembly.AddTeam(new List<Registration> { registration });
                added++;
            }

            return added;
        }
    }
}
