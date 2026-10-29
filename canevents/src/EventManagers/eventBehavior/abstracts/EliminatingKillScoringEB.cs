using canevents.src.EventManagers.eventBehavior.Shared;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;

namespace canevents.src.EventManagers.eventBehavior.abstracts
{
    // One-life modes. Eliminates after the kill feed and scoreboard, so a match-ending death is still reported.
    public abstract class EliminatingKillScoringEB : KillScoringEB
    {
        protected EliminatingKillScoringEB(string name, JsonObject attributes, RunningEvent runningEvent)
            : base(name, attributes, runningEvent) { }

        // EliminatePlayer sets ShouldFinish when one group is left; default GetWinners() handles the rest.
        protected sealed override void AfterScoredDeath(string victimUid, Entity victimBody, KillInfo kill)
        {
            runningEvent.EliminatePlayer(victimUid);
            AfterEliminated(victimUid, victimBody, kill);
        }

        protected virtual void AfterEliminated(string victimUid, Entity victimBody, KillInfo kill) { }
    }
}
