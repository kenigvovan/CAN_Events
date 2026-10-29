using System;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace canevents.src.EventManagers.events
{
    public abstract class CANEventBehavior
    {
        // name is unused but the signature is what Activator and Clone() expect.
        public CANEventBehavior(string name, JsonObject attributes, RunningEvent runningEvent)
        {
            Attributes = attributes;
            this.runningEvent = runningEvent;
        }
        public abstract string Name { get; }

        // OnTick at 1 Hz. The old JSON "RegisterGameTickListener" key was never read.
        public virtual bool WantsTick => false;

        // Setter drops the cached Config reader.
        public JsonObject Attributes
        {
            get => attributes;
            set { attributes = value; config = null; }
        }
        private JsonObject attributes;
        public RunningEvent runningEvent { get; set; }

        // Logs missing keys, unlike raw Attributes access. Works without a logger.
        protected BehaviorAttributes Config => config ??= new BehaviorAttributes(
            Attributes, runningEvent?.sapi?.World?.Logger, Name);

        private BehaviorAttributes config;

        // Scalar keys read during Initialize; templates are initialized on load, so this is filled there.
        public System.Collections.Generic.IReadOnlyList<KnownSetting> KnownSettings => Config.Known;

        protected Log log => logger ??= Log.For(runningEvent?.sapi?.World?.Logger, Name);

        private Log logger;

        protected void Announce(string langKey, params object[] args)
            => eventBehavior.Shared.EventBroadcaster.Broadcast(runningEvent, Lang.Get(langKey, args));

        // System.Func spelled out: Vintagestory.API.Common has a Func<> of its own.
        protected void AnnounceWinners(string winKey, string noneKey, System.Func<string, string> entry)
        {
            var winners = runningEvent.GetWinners();
            if (winners.Count > 0) Announce(winKey, string.Join(", ", winners.Select(entry)));
            else if (noneKey != null) Announce(noneKey);
        }

        public virtual void Initialize() { }
        public virtual void OnBeforeStart() { }
        public virtual void OnStart() { }
        public virtual void OnTick(float dt) { }
        public virtual void OnEnd() { }
        // body is passed because BodyOf skips dead bots. ArenaBotsEB eliminates the bot afterwards.
        public virtual void OnParticipantDeath(string uid, Entity body, DamageSource source) { }

        // True silences ArenaBotsEB's death messages.
        public virtual bool AnnouncesDeaths => false;

        public virtual void OnParticipantRespawn(IServerPlayer player) { }
        public virtual void OnParticipantLeftGame() { }
        public virtual void OnParticipantJoinedGame() { }

        // A player entering or leaving a match that is already running (open matches).
        // Admitted runs in behavior order; returning false from CanAdmit anywhere cancels the entry.
        public virtual bool CanAdmit(IServerPlayer player) => true;
        public virtual void OnParticipantAdmitted(IServerPlayer player) { }
        public virtual void OnParticipantWithdrawn(IServerPlayer player) { }

        // Disconnected out of an open match: drop per-player state only. Inventory and position
        // come back on reconnect (RestoreEventBackup, RestoreOrphanedReturnPosition).
        public virtual void OnParticipantDropped(string uid) { }

        // RunningEvent sets runningEvent on the clone, then calls Initialize().
        public virtual CANEventBehavior Clone()
        {
            return (CANEventBehavior)Activator.CreateInstance(GetType(), Name, Attributes, runningEvent);
        }

        // Last chance for SetWinners before FinishEvent; avoids racing the manager's time check.
        public virtual void OnBeforeFinish() { }

        // Scoring lives in Shared.IMatchScorer (RunningEvent.Scorer), not here.
    }
}
