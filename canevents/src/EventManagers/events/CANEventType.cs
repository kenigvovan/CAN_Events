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
        // Whether this event counts toward ranking (leaderboard, Elo). Rewards are independent,
        // configured per event via Reward.
        [JsonProperty]
        public bool Rated { get; set; } = true;
        [JsonProperty]
        public string EventType { get; set; }
        [JsonProperty]
        public string Category { get; set; }
        [JsonProperty]
        public string[] EventPlaceTypes { get; set; }

        // Optional per-event reward override; unset fields fall back to global Config.
        [JsonProperty]
        public RewardSpec Reward { get; set; }

        // Optional per-event entry fee / wager pool; null = no fee.
        [JsonProperty]
        public EntryFeeSpec EntryFee { get; set; }

        // Optional match modifiers, e.g. ["noarmor", "health:0.5", "loadout:Archer"]. Validated
        // against EventMutators on load; unknown entries are dropped with a log warning.
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
