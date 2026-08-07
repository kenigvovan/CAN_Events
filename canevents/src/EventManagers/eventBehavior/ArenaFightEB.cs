using System.Collections.Generic;
using canevents.src.EventManagers.eventBehavior.abstracts;
using canevents.src.EventManagers.eventBehavior.Shared;
using canevents.src.EventManagers.events;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace canevents.src.EventManagers.eventBehavior
{
    // Last-man-standing arena fight: one life, eliminated on death.
    // Kill/death tallies + loadout handling live in KillScoringEB.
    public class ArenaFightEB : KillScoringEB
    {
        public ArenaFightEB(string name, JsonObject attributes, RunningEvent runningEvent)
            : base(name, attributes, runningEvent) { }

        public override string Name { get => "ArenaFight"; set { } }

        public override void Initialize() => ParseLoadoutSets();

        public override void OnBeforeStart() => InitScores();

        public override void OnStart() => GiveLoadoutAndHealAll();

        // EliminatePlayer flips ShouldFinish once one group is left, so the winner needs no
        // explicit resolution here.
        protected override void OnScoredDeath(IServerPlayer victim, KillInfo kill)
        {
            runningEvent.EliminatePlayer(victim.PlayerUID);

            EventBroadcaster.Broadcast(runningEvent,
                Lang.Get("canevents:player-killed-by", victim.PlayerName, kill.KillerName, runningEvent.ParticipantsStillInEvent.Count));
        }

        protected override void AfterScoredDeath(IServerPlayer victim, KillInfo kill)
            => runningEvent.sapi.World.Logger.Notification("Arena: {0} eliminated by {1}", victim.PlayerName, kill.KillerName);

        public override void OnEnd()
        {
            var winners = runningEvent.GetWinners();
            if (winners.Count > 0)
            {
                var winnerNames = new List<string>();
                foreach (var uid in winners)
                {
                    var pl = runningEvent.sapi.World.PlayerByUid(uid);
                    if (pl != null)
                    {
                        int kills = killCounts.ContainsKey(uid) ? killCounts[uid] : 0;
                        winnerNames.Add(Lang.Get("canevents:arena-winner-with-kills", pl.PlayerName, kills));
                    }
                }
                EventBroadcaster.Broadcast(runningEvent, Lang.Get("canevents:arena-winner", string.Join(", ", winnerNames)));
            }
            else
            {
                EventBroadcaster.Broadcast(runningEvent, Lang.Get("canevents:arena-no-winner"));
            }
        }
    }
}
