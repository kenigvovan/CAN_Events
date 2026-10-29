using canevents.src.EventManagers.events;
using Vintagestory.API.Datastructures;

namespace canevents.src.EventManagers.eventBehavior
{
    // Marker: arenas this event can be held in are protected from block edits, during its matches and,
    // unless turned off, between them. The work is done by ArenaProtection, which watches the bounds of
    // every such place; a behavior alone would only live as long as a match. Leave it out of modes where
    // breaking blocks is the game (Spleef). Both switches show up in the admin settings editor.
    public class ArenaProtectionEB : CANEventBehavior
    {
        public ArenaProtectionEB(string name, JsonObject attributes, RunningEvent runningEvent)
            : base(name, attributes, runningEvent) { }

        public override string Name => BehaviorNames.ArenaProtection;

        public bool Enabled { get; private set; } = true;
        public bool BetweenMatches { get; private set; } = true;

        public override void Initialize()
        {
            Enabled = Config.Bool("protectArena", true);
            BetweenMatches = Config.Bool("protectBetweenMatches", true);
            Config.WarnUnknown();
        }
    }
}
