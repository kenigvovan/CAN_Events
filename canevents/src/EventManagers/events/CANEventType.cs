using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using canevents.src.EventManagers.eventBehavior.abstracts;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace canevents.src.EventManagers.events
{
    public class CANEventType
    {
        [JsonProperty]
        public string Code { get; set; }
        [JsonProperty]
        public bool Enabled { get; set; } = true;
        // Counts toward leaderboard / Elo. Rewards don't depend on it.
        [JsonProperty]
        public bool Rated { get; set; } = true;
        [JsonProperty]
        public string EventType { get; set; }
        [JsonProperty]
        public string Category { get; set; }
        [JsonProperty]
        public string[] EventPlaceTypes { get; set; }

        // "Match found" to teleport. Null = global countdownSeconds; at least 10s with a loadout popup.
        [JsonProperty]
        public int? CountdownSeconds { get; set; }

        [JsonProperty]
        public RewardSpec Reward { get; set; }

        // Null = no fee.
        [JsonProperty]
        public EntryFeeSpec EntryFee { get; set; }

        // e.g. ["noarmor", "health:0.5", "loadout:Archer"]. Unknown entries are dropped on load.
        [JsonProperty]
        public string[] Mutators { get; set; }

        [JsonProperty]
        public List<CANEventBehavioreType> Behaviors { get; set; }

        [JsonProperty, JsonConverter(typeof(JsonAttributesConverter))]
        public JsonObject Attributes;
        public void InitType()
        {

        }
    }
}
