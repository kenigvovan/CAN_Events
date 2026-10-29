using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using canevents.src.EventManagers.eventBehavior.abstracts;
using canevents.src.EventPlacesManagers.Places;

namespace canevents.src.EventManagers.events
{
    public class CANEvent
    {
        public string Code;
        public string Category;
        // Counts toward leaderboard / Elo. Rewards don't depend on it.
        public bool Rated = true;
        // Null = Config.countdownSeconds. At least 10s when a loadout pick popup opens.
        public int? CountdownSeconds;
        // Null = global Config.
        public RewardSpec Reward;
        // Null = no fee.
        public EntryFeeSpec EntryFee;
        public List<CANEventBehavior> Behaviors = new List<CANEventBehavior>();
        // Looked up, not stored: a stored ref survived Clone() pointing at the template's instance.
        public PartyAssemblingEB AssemblingEB => GetBehavior<PartyAssemblingEB>();
        public HashSet<string> EventTypes { get; set; } = new HashSet<string>();

        // key -> value, null for bare flags. A duel adds the challenger's picks onto its clone.
        public Dictionary<string, string> Mutators = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public bool HasMutator(string key) => Mutators.ContainsKey(key);

        public string MutatorValue(string key)
            => Mutators.TryGetValue(key, out var v) ? v : null;

        public float MutatorFloat(string key, float fallback)
            => EventMutators.FloatOf(Mutators, key, fallback);

        public bool CodeEquals(string code)
            => !string.IsNullOrEmpty(code) && Code != null
               && Code.Equals(code, StringComparison.OrdinalIgnoreCase);

        public T GetBehavior<T>() where T : class => FirstBehavior<T>(Behaviors);

        // Shared with RunningEvent.GetBehavior.
        internal static T FirstBehavior<T>(List<CANEventBehavior> behaviors) where T : class
        {
            if (behaviors == null) return null;
            for (int i = 0; i < behaviors.Count; i++)
                if (behaviors[i] is T match) return match;
            return null;
        }
        public CANEvent Clone()
        {
            CANEvent clone = (CANEvent)this.MemberwiseClone();
            // Own copy so duel mutators don't leak into the queue template.
            clone.Mutators = new Dictionary<string, string>(Mutators, StringComparer.OrdinalIgnoreCase);
            clone.Behaviors = new List<CANEventBehavior>();
            foreach (var behavior in Behaviors)
            {
                clone.Behaviors.Add(behavior.Clone());
            }
            return clone;
        }
    }
}
