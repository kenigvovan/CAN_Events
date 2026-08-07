using System;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace canevents.src.EventManagers.events
{
    public abstract class CANEventBehavior
    {
        public CANEventBehavior(string name, JsonObject attributes, RunningEvent runningEvent)
        {
            this.Name = name;
            Attributes = attributes;
            this.runningEvent = runningEvent;
            this.RegisterGameTickListener = Attributes["RegisterGameTickListener"].AsBool(true);
        }
        public abstract string Name { get; set; }
        public bool RegisterGameTickListener { get; set; }
        public JsonObject Attributes { get; set; }
        public RunningEvent runningEvent { get; set; }

        /// <summary>
        /// Configuration reader that reports what it could not find. Use it in Initialize() rather
        /// than reaching into Attributes directly, where a misspelt key silently becomes a default.
        /// Null-logger safe, so a behavior built outside a match still reads its configuration.
        /// </summary>
        protected BehaviorAttributes Config => config ??= new BehaviorAttributes(
            Attributes, runningEvent?.sapi?.World?.Logger, Name);

        private BehaviorAttributes config;

        /// <summary>The server log, already tagged with this behavior's name. Same null-safety as
        /// Config: no logger, no output.</summary>
        protected Log log => logger ??= Log.For(runningEvent?.sapi?.World?.Logger, Name);

        private Log logger;

        // Only Name is mandatory. Every lifecycle hook is a virtual no-op so a behavior implements
        // just the phases it cares about.
        public virtual void Initialize() { }
        public virtual void OnBeforeStart() { }
        public virtual void OnStart() { }
        public virtual void OnTick(float dt) { }
        public virtual void OnEnd() { }
        public virtual void OnParticipantDeath(IServerPlayer player, DamageSource source) { }
        public virtual void OnParticipantRespawn(IServerPlayer player) { }
        public virtual void OnParticipantLeftGame() { }
        public virtual void OnParticipantJoinedGame() { }

        // RunningEvent sets clone.runningEvent then calls clone.Initialize(), so Initialize() always
        // has a non-null runningEvent. Override only when a behavior has state not re-derivable from
        // Attributes (e.g. Spleef's platform blocks); Initialize() still runs after Clone().
        public virtual CANEventBehavior Clone()
        {
            return (CANEventBehavior)Activator.CreateInstance(GetType(), Name, Attributes, runningEvent);
        }

        // Called on each behavior just before FinishEvent runs. Last chance to call
        // runningEvent.SetWinners(...), used by time-based modes to resolve time-up winners without
        // racing the manager's own time check.
        public virtual void OnBeforeFinish() { }

        // Match scoring/ranking is NOT on the base behavior; the mode behavior implements
        // Shared.IMatchScorer instead (resolved as RunningEvent.Scorer).
    }
}
