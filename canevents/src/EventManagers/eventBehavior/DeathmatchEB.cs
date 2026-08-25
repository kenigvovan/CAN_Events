using canevents.src.EventManagers.eventBehavior.abstracts;
using canevents.src.EventManagers.eventBehavior.Shared;
using canevents.src.EventManagers.events;
using canevents.src.HarmonyPatches;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace canevents.src.EventManagers.eventBehavior
{
    // Team Deathmatch: players respawn after death, teams score frags.
    // Win = first team to killLimit, or highest score when timeLimitSeconds elapses.
    public class DeathmatchEB : TeamScoreEB, IProvidesRespawnPoint
    {
        private int killLimit = 30;

        public DeathmatchEB(string name, JsonObject attributes, RunningEvent runningEvent)
            : base(name, attributes, runningEvent) { }

        public override string Name { get => "Deathmatch"; set { } }

        public override void Initialize()
        {
            ParseTimeLimit(300f);
            killLimit = Config.Int("killLimit", 30);
            ParseLoadoutSets();
            Config.WarnUnknown();
        }

        public override void OnBeforeStart() => BeginMatch();

        public override void OnStart() => GiveLoadoutAndHealAll();

        protected override void OnScoredDeath(IServerPlayer victim, KillInfo kill)
        {
            if (kill.HasKiller && !kill.TeamKill && kill.KillerUid != victim.PlayerUID
                && kill.KillerTeam >= 0 && kill.KillerTeam < teamScores.Length)
            {
                teamScores[kill.KillerTeam]++;
            }

            // No EliminatePlayer: the player respawns via the vanilla flow and OnParticipantRespawn
            // re-equips them.

            EventBroadcaster.Broadcast(runningEvent,
                Lang.Get("canevents:deathmatch-kill-score", victim.PlayerName, kill.KillerName, ScoreLine()));
        }

        protected override void AfterScoredDeath(IServerPlayer victim, KillInfo kill)
        {
            if (!MatchResolved && kill.KillerTeam >= 0 && kill.KillerTeam < teamScores.Length
                && teamScores[kill.KillerTeam] >= killLimit)
            {
                DeclareTeamWinner(kill.KillerTeam);
            }
        }

        public override void OnParticipantRespawn(IServerPlayer player)
        {
            if (!runningEvent.IsActive(player.PlayerUID)) return;
            // TPThereAndBack handles teleport on its own OnParticipantRespawn; we re-equip + heal.
            GiveLoadoutAndHeal(player);
        }

        // OnBeforeFinish (time-up winner) comes from TeamScoreEB - it is the same in every team mode.

        public override void OnEnd()
            => EventBroadcaster.Broadcast(runningEvent,
                Lang.Get("canevents:deathmatch-ended", ScoreLine()));

        public bool TryGetRespawnPoint(string playerUid, out PlayerSpawnPos pos)
            => EventSpawns.TryGetRandom(runningEvent, out pos);
    }
}
