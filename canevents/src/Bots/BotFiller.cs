using System;
using System.Collections.Generic;
using canevents.src.EventManagers;
using canevents.src.EventManagers.events;
using canevents.src.QueueManagers;
using Vintagestory.API.Common;

namespace canevents.src.Bots
{
    // Fills a lobby with bots right before the match starts. Each bot is a solo side.
    public static class BotFiller
    {
        // 0 if the event has no ArenaBots behavior.
        public static int SidesItCanFill(CANEvent canEvent)
        {
            var assembling = canEvent?.AssemblingEB;
            if (assembling == null) return 0;
            if (canEvent.GetBehavior<EventManagers.eventBehavior.ArenaBotsEB>() == null) return 0;
            return Math.Max(0, Math.Min(assembling.BotFillToParties, assembling.MaxPartiesCount));
        }

        public static int Fill(CANEvent canEvent, TeamAssembly assembly, ILogger logger = null)
        {
            var assembling = canEvent?.AssemblingEB;
            if (assembling == null || assembly == null) return 0;

            int fillTo = Math.Min(assembling.BotFillToParties, assembling.MaxPartiesCount);
            if (fillTo <= 0 || assembly.Shards.Count >= fillTo) return 0;

            // Without ArenaBots the bots get no bodies and the match can't end.
            if (canEvent.GetBehavior<EventManagers.eventBehavior.ArenaBotsEB>() == null)
            {
                logger?.Warning("[bots] Event {0} sets botFillToParties but has no {1} behavior; not filling with bots.",
                    canEvent.Code, BehaviorNames.ArenaBots);
                return 0;
            }

            int added = 0;
            while (assembly.Shards.Count < fillTo)
            {
                var registration = new Registration(canEvent, new List<string> { BotIdentity.NewUid(added) })
                {
                    // Never was in the queue store.
                    MarkedUsed = true
                };
                assembly.AddTeam(new List<Registration> { registration });
                added++;
            }

            return added;
        }
    }
}
