using canevents.src.EventManagers.events;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Datastructures;

namespace canevents.src.Bots
{
    // Without ArenaBots, bot uids have no bodies and the match can't end until the time limit.
    // So we add the behavior to a clone of the event when it's missing.
    public static class BotMatchSupport
    {
        // Returns ev itself if it already has ArenaBots.
        public static CANEvent EnsureBotsCanSpawn(CANEvent ev)
        {
            if (ev == null) return null;
            if (ev.GetBehavior<EventManagers.eventBehavior.ArenaBotsEB>() != null) return ev;

            var clone = ev.Clone();
            clone.Behaviors.Add(new EventManagers.eventBehavior.ArenaBotsEB(
                BehaviorNames.ArenaBots, DefaultAttributes(), null));
            return clone;
        }

        // Copied from the shipped Hunger Games bot config.
        public static JsonObject DefaultAttributes()
        {
            var token = JObject.Parse(@"{
                ""health"": 15,
                ""meleeDamage"": 2,
                ""weapons"": [
                    ""game:knife-generic-copper"",
                    ""game:spear-generic-flint"",
                    ""game:club-generic-wood""
                ]
            }");
            return new JsonObject(token);
        }
    }
}
