using canevents.src.EventManagers;
using canevents.src.EventManagers.eventBehavior.abstracts;
using Vintagestory.API.Datastructures;

namespace canevents.src.EventManagers.eventBehavior
{
    public class ByPartsPartyCollected : PartyAssemblingEB
    {
        public ByPartsPartyCollected(string name, JsonObject attributes, RunningEvent runningEvent)
            : base(name, attributes, runningEvent) { }

        public override string Name { set { } get => "ByPartsPartyCollected"; }

        public override void Initialize()
        {
            this.MaxPartiesCount = Config.Int("maxPartiesCount", 2);
            this.MinPartiesCount = Config.Int("minPartiesCount", 2);
            this.MaxPartySize = Config.Int("maxPartySize", 1);
            this.MinPartySize = Config.Int("minPartySize", 1);
            this.BotFillToParties = Config.Int("botFillToParties", 0);
            // No queue-fill wait here, but configs copied from FullPartyRequired carry the key;
            // accept it silently instead of reporting a typo.
            Config.Has("queueFillSeconds");
            Config.WarnUnknown();
        }
    }
}
