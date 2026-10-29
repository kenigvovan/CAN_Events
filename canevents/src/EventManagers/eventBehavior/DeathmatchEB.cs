using canevents.src.EventManagers.eventBehavior.abstracts;
using canevents.src.EventManagers.eventBehavior.Shared;
using canevents.src.EventManagers.events;
using canevents.src.HarmonyPatches;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace canevents.src.EventManagers.eventBehavior
{
    // Team deathmatch with respawns. First to killLimit, else highest at time up.
    public class DeathmatchEB : TeamScoreEB
    {
        private int killLimit = 30;

        public DeathmatchEB(string name, JsonObject attributes, RunningEvent runningEvent)
            : base(name, attributes, runningEvent) { }

        public override string Name => BehaviorNames.Deathmatch;

        protected override float DefaultTimeLimitSeconds => 300f;

        protected override void ReadModeConfig() => killLimit = Config.Int("killLimit", 30);

        public override void OnBeforeStart() => BeginMatch();

        public override void OnStart() => GiveLoadoutAndHealAll();

        protected override void OnScoredDeath(string victimUid, Entity victimBody, KillInfo kill)
        {
            CreditKillerTeam(kill);

            // Bots don't respawn; ArenaBotsEB removes them after this.
            Announce("canevents:deathmatch-kill-score", runningEvent.NameOf(victimUid), kill.KillerName, ScoreLine());
        }

        protected override void AfterScoredDeath(string victimUid, Entity victimBody, KillInfo kill)
        {
            if (!MatchResolved && IsTeamIndex(kill.KillerTeam) && teamScores[kill.KillerTeam] >= killLimit)
            {
                DeclareTeamWinner(kill.KillerTeam);
            }
        }

        protected override string EndedLangKey => "canevents:deathmatch-ended";

        // No base to defend, respawn anywhere.
        protected override bool RespawnAtTeamSpawn => false;
    }
}
