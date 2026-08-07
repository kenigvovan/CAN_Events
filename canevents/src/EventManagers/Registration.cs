using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using canevents.src.EventManagers.events;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace canevents.src.EventManagers
{
    public class Registration
    {
        public CANEvent Event { get; set; }
        public string RegistrationGuid { get; set; }
        public long TimeStampCreated { get; set; }
        public List<string> PlayersGuids { get; set; }
        public int PlayersCount => PlayersGuids.Count;
        public bool MarkedUsed { get; set; } = false;
        public string ChosenLoadoutSet { get; set; }
        public string RegistrantUid { get; set; }
        /// <summary>Per-member loadout picks made at queue time; overrides ChosenLoadoutSet for that uid.</summary>
        public Dictionary<string, string> PerPlayerLoadoutChoices { get; set; } = new Dictionary<string, string>();

        public Registration(CANEvent canEvent, List<string> playersGuids)
        {
            this.RegistrationGuid = Guid.NewGuid().ToString();
            this.Event = canEvent;
            this.TimeStampCreated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            this.PlayersGuids = playersGuids;
        }
    }
}
