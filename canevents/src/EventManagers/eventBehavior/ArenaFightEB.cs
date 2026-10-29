using System.Collections.Generic;
using canevents.src.EventManagers.eventBehavior.abstracts;
using canevents.src.EventManagers.eventBehavior.Shared;
using canevents.src.EventManagers.events;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace canevents.src.EventManagers.eventBehavior
{
    // Last man standing, one life.
    public class ArenaFightEB : EliminatingKillScoringEB
    {
        public ArenaFightEB(string name, JsonObject attributes, RunningEvent runningEvent)
            : base(name, attributes, runningEvent) { }

        public override string Name => BehaviorNames.ArenaFight;

        public override void OnBeforeStart() => InitScores();

        public override void OnStart() => GiveLoadoutAndHealAll();

        // After the elimination, so the count is of those still standing.
        protected override void AfterEliminated(string victimUid, Entity victimBody, KillInfo kill)
        {
            Announce("canevents:player-killed-by", runningEvent.NameOf(victimUid), kill.KillerName, runningEvent.ParticipantsStillInEvent.Count);
            log.Notification("{0} eliminated by {1}", runningEvent.NameOf(victimUid), kill.KillerName);
        }

        public override void OnEnd()
            => AnnounceWinners("canevents:arena-winner", "canevents:arena-no-winner",
                uid => Lang.Get("canevents:arena-winner-with-kills", runningEvent.NameOf(uid), KillsOf(uid)));
    }
}
