using canevents.src.EventManagers.events;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Datastructures;

namespace canevents.src.Bots
{
    /// <summary>
    /// Makes an event able to field bots without every mode's config declaring ArenaBots. Bot uids
    /// in a mode lacking that behavior produce a match full of ghosts: nobody can be killed, so it
    /// never ends and blocks its arena until the time limit. Fixed by grafting the behavior onto a
    /// throwaway copy of the event, leaving the config files untouched.
    /// </summary>
    public static class BotMatchSupport
    {
        /// <summary>
        /// The event as-is when it can already spawn bots, otherwise a copy that can (so a normal
        /// human match is never cloned).
        /// </summary>
        public static CANEvent EnsureBotsCanSpawn(CANEvent ev)
        {
            if (ev == null) return null;
            if (ev.GetBehavior<EventManagers.eventBehavior.ArenaBotsEB>() != null) return ev;

            var clone = ev.Clone();
            clone.Behaviors.Add(new EventManagers.eventBehavior.ArenaBotsEB(
                "ArenaBots", DefaultAttributes(), null));
            return clone;
        }

        /// <summary>
        /// Stats from the shipped Hunger Games bot config. Weapon codes are copied from it, not
        /// guessed: an invented code does not resolve and the bot fights bare-handed.
        /// </summary>
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
