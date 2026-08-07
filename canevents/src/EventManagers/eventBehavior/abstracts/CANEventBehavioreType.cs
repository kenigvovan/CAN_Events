using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace canevents.src.EventManagers.eventBehavior.abstracts
{
    public class CANEventBehavioreType
    {
        [JsonProperty]
        public string Name { get; set; }
        [JsonProperty, JsonConverter(typeof(JsonAttributesConverter))]
        public JsonObject Attributes;
    }
}
