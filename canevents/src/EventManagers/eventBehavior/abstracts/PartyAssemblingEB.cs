using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using canevents.src.EventManagers;
using canevents.src.EventManagers.events;
using Vintagestory.API.Datastructures;

namespace canevents.src.EventManagers.eventBehavior.abstracts
{
    public abstract class PartyAssemblingEB : CANEventBehavior, IPartyCapacityProvider
    {
        public int MaxPartySize { get; set; } = 1;
        public int MinPartySize { get; set; } = 1;
        public int MaxPartiesCount { get; set; } = 1;
        public int MinPartiesCount { get; set; } = 1;
        // Seconds to wait after MinPartiesCount is reached before starting with fewer than Max.
        // 0 = start immediately when minimum is met.
        public int QueueFillSeconds { get; set; } = 0;
        /// <summary>
        /// Fill the lobby with bots up to this many sides before the match starts. 0 = no bots,
        /// capped by MaxPartiesCount.
        /// </summary>
        public int BotFillToParties { get; set; } = 0;
        protected PartyAssemblingEB(string name, JsonObject attributes, RunningEvent runningEvent) : base(name, attributes, runningEvent)
        {
        }

        public int GetMaxPlayers() => MaxPartiesCount * MaxPartySize;
        public int GetMinPlayers() => MinPartiesCount * MinPartySize;
    }
}
