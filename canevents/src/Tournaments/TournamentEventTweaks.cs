using canevents.src.EventManagers.events;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Datastructures;

namespace canevents.src.Tournaments
{
    // Tweaks go into Attributes because RunningEvent rebuilds behaviors from (Name, Attributes).
    // Deep copy: cloned behaviors share the Attributes instance with the original.
    public static class TournamentEventTweaks
    {
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

            return ev;
        }
    }
}
