using canevents.src.EventManagers.events;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Datastructures;

namespace canevents.src.Tournaments
{
    /// <summary>
    /// Per-tournament adjustments to a mode, applied to a throwaway copy of the event. Configuration
    /// must go into a behavior's Attributes, not a property: RunningEvent rebuilds behaviors from
    /// (Name, Attributes) for every match, so only the attributes survive. The token is deep-copied
    /// because cloned behaviors share their Attributes instance with the real mode.
    /// </summary>
    public static class TournamentEventTweaks
    {
        /// <summary>
        /// Copy of the event with SavePlayerAttributes.KeepOwnGear on, or the event itself when it has
        /// no such behavior.
        /// </summary>
        public static CANEvent WithOwnGear(CANEvent ev) => WithBehaviorFlag(ev, "keepOwnGear", true);

        private static CANEvent WithBehaviorFlag(CANEvent ev, string attribute, bool value)
        {
            if (ev == null) return null;

            var clone = ev.Clone();
            for (int i = 0; i < clone.Behaviors.Count; i++)
            {
                if (!(clone.Behaviors[i] is EventManagers.eventBehavior.SavePlayerAttributes)) continue;

                var behavior = clone.Behaviors[i];
                var source = behavior.Attributes?.Token as JObject;
                var edited = source != null ? (JObject)source.DeepClone() : new JObject();
                edited[attribute] = value;

                clone.Behaviors[i] = new EventManagers.eventBehavior.SavePlayerAttributes(
                    behavior.Name, new JsonObject(edited), null);
                return clone;
            }

            return ev;   // nothing to configure: hand back the original, no copy needed
        }
    }
}
