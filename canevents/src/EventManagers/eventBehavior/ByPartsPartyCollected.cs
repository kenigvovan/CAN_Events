using canevents.src.EventManagers;
using canevents.src.EventManagers.eventBehavior.abstracts;
using canevents.src.EventManagers.events;
using Vintagestory.API.Datastructures;

namespace canevents.src.EventManagers.eventBehavior
{
    public class ByPartsPartyCollected : PartyAssemblingEB
    {
        public ByPartsPartyCollected(string name, JsonObject attributes, RunningEvent runningEvent)
            : base(name, attributes, runningEvent) { }

        public override string Name => BehaviorNames.ByPartsPartyCollected;

        public override void Initialize()
        {
            this.MaxPartiesCount = Config.Int("maxPartiesCount", 2);
            this.MinPartiesCount = Config.Int("minPartiesCount", 2);
            this.MaxPartySize = Config.Int("maxPartySize", 1);
            this.MinPartySize = Config.Int("minPartySize", 1);
            this.BotFillToParties = Config.Int("botFillToParties", 0);
            // Unused here, but configs copied from FullPartyRequired have it; mark as read.
            Config.Has("queueFillSeconds");
            Config.WarnUnknown();
        }
    }
}
