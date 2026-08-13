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
        // Whether this event counts toward ranking (leaderboard / Elo). Rewards are independent.
        public bool Rated = true;
        // Optional per-event reward override; null falls back to global Config.
        public RewardSpec Reward;
        // Optional per-event entry fee / wager pool; null = no fee.
        public EntryFeeSpec EntryFee;
        public List<CANEventBehavior> Behaviors = new List<CANEventBehavior>();
        public PartyAssemblingEB AssemblingEB { get; set; }
        public HashSet<string> EventTypes { get; set; } = new HashSet<string>();

        /// <summary>Active match modifiers, parsed key -> value (null for bare flags). Filled from the
        /// event JSON's "mutators" array; a duel adds the challenger's picks onto its clone.</summary>
        public Dictionary<string, string> Mutators = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public bool HasMutator(string key) => Mutators.ContainsKey(key);

        /// <summary>The mutator's value, or null when it is unset or a bare flag.</summary>
        public string MutatorValue(string key)
            => Mutators.TryGetValue(key, out var v) ? v : null;

        public float MutatorFloat(string key, float fallback)
            => EventMutators.FloatOf(Mutators, key, fallback);

        /// <summary>Is this the event with that code? Case-insensitive, like every other code compare
        /// in the mod: a code from stored data or a client field must still match its own event.</summary>
        public bool CodeEquals(string code)
            => !string.IsNullOrEmpty(code) && Code != null
               && Code.Equals(code, StringComparison.OrdinalIgnoreCase);

        public T GetBehavior<T>() where T : class
        {
            for (int i = 0; i < Behaviors.Count; i++)
            {
                if (Behaviors[i] is T)
                {
                    return Behaviors[i] as T;
                }
            }

            return null;
        }
        public CANEvent Clone()
        {
            CANEvent clone = (CANEvent)this.MemberwiseClone();
            // Own copy: a duel switches mutators on its clone, and those must never bleed into the
            // queue's template of the same event.
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
