using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers.events;
using Vintagestory.API.Datastructures;

namespace canevents.src.EventManagers.eventBehavior.abstracts
{
    // Deathmatch, KingOfTheHill and CTF.
    public abstract class TeamScoreEB : RespawningKillScoringEB
    {
        protected int[] teamScores = new int[0];

        protected TeamScoreEB(string name, JsonObject attributes, RunningEvent runningEvent)
            : base(name, attributes, runningEvent) { }

        protected void InitTeamScores()
            => teamScores = new int[runningEvent.ParticipantsByGroupsBefore.Count];

        // After InitScores, which fixes the roster.
        protected override void BeginMatch()
        {
            base.BeginMatch();
            InitTeamScores();
        }

        protected string ScoreLine() => Shared.ScoringUtil.ScoreLine(teamScores);

        // {0} = score line.
        protected abstract string EndedLangKey { get; }

        // Modes with own teardown do it first, then call base.
        public override void OnEnd() => Announce(EndedLangKey, ScoreLine());

        protected override bool RespawnAtTeamSpawn => true;

        // Time-up. Earlier resolution (KOTH score limit) is guarded by the latch in ResolveMatch.
        public override void OnBeforeFinish() => DeclareTeamWinner(ResolveWinnerTeam());

        public override Shared.ScoreboardData BuildScoreboard()
        {
            var data = base.BuildScoreboard();
            data.TeamScores = teamScores;
            return data;
        }

        protected bool IsTeamIndex(int team) => team >= 0 && team < teamScores.Length;

        // Clean kill: a point to the killer's team.
        protected void CreditKillerTeam(Shared.KillInfo kill)
        {
            if (kill.IsClean && IsTeamIndex(kill.KillerTeam)) teamScores[kill.KillerTeam]++;
        }

        // -1 on tie or no teams.
        protected int ResolveLeadingTeam() => Shared.ScoringUtil.LeadingTeam(teamScores);

        // Last team standing wins by forfeit regardless of score.
        protected int ResolveWinnerTeam()
        {
            var remaining = runningEvent.ParticipantsByGroupsStillInEvent;
            if (remaining.Count == 1)
                return runningEvent.TeamIndexOf(remaining[0].FirstOrDefault());
            return ResolveLeadingTeam();
        }

        // Out-of-range index = draw. Idempotent.
        protected void DeclareTeamWinner(int winnerTeam)
        {
            bool valid = winnerTeam >= 0 && winnerTeam < runningEvent.ParticipantsByGroupsBefore.Count;
            ResolveMatch(valid ? runningEvent.ParticipantsByGroupsBefore[winnerTeam] : null,
                "team score resolved");
        }

        // Winner first (forfeit wins can trail on score), then score. Ties share a bracket.
        public override List<HashSet<string>> GetFinalRanking()
            => Shared.ScoringUtil.RankTeams(runningEvent.ParticipantsByGroupsBefore, runningEvent.GetWinners(), teamScores);
    }
}
