using canevents.src.EventManagers;
using canevents.src.EventManagers.eventBehavior.abstracts;
using canevents.src.EventManagers.events;
using Vintagestory.API.Datastructures;

namespace canevents.src.EventManagers.eventBehavior
{
    public class FullPartyRequired : PartyAssemblingEB
    {
        public FullPartyRequired(string name, JsonObject attributes, RunningEvent runningEvent)
            : base(name, attributes, runningEvent) { }

        public override string Name => BehaviorNames.FullPartyRequired;

        public override void Initialize()
        {
            this.MaxPartiesCount   = Config.Int("maxPartiesCount", 1);
            this.MinPartiesCount   = Config.Int("minPartiesCount", 1);
            this.MaxPartySize      = Config.Int("maxPartySize", 1);
            this.MinPartySize      = Config.Int("minPartySize", 1);
            this.QueueFillSeconds  = Config.Int("queueFillSeconds", 0);
            this.BotFillToParties  = Config.Int("botFillToParties", 0);
            Config.WarnUnknown();
        }
    }
}
