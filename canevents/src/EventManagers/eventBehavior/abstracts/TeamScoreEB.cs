using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers.events;
using Vintagestory.API.Datastructures;

namespace canevents.src.EventManagers.eventBehavior.abstracts
{
    /// <summary>
    /// Per-team scoring plus score-limit / time-up winner resolution on top of
    /// <see cref="KillScoringEB"/>. Used by Deathmatch and KingOfTheHill.
    /// </summary>
    public abstract class TeamScoreEB : KillScoringEB
    {
        protected int[] teamScores = new int[0];

        protected TeamScoreEB(string name, JsonObject attributes, RunningEvent runningEvent)
            : base(name, attributes, runningEvent) { }

        /// <summary>Size the team-score array to the number of groups. Call from OnBeforeStart().</summary>
        protected void InitTeamScores()
            => teamScores = new int[runningEvent.ParticipantsByGroupsBefore.Count];

        /// <summary>Team modes open a match the same way, plus their score array - strictly after
        /// InitScores, which is what fixes the roster this array is sized from.</summary>
        protected override void BeginMatch()
        {
            base.BeginMatch();
            InitTeamScores();
        }

        /// <summary>The score line every team mode prints when the match is over ("3 : 1").</summary>
        protected string ScoreLine() => string.Join(" : ", teamScores);

        // Time is up and nobody resolved the match on their own terms. Not virtual: a mode that
        // resolves earlier (KOTH on its score limit) is protected by the latch inside ResolveMatch,
        // so there is nothing here for a mode to override.
        public override void OnBeforeFinish() => DeclareTeamWinner(ResolveWinnerTeam());

        // Team-mode scoreboard carries the per-team scores too.
        protected override void PushScoreboard()
            => runningEvent.NetworkHandler?.SendScoreboard(runningEvent, killCounts, deathCounts, teamScores);

        /// <summary>Index of the single highest-scoring team, or -1 on a tie / no teams.</summary>
        protected int ResolveLeadingTeam()
            => Shared.ScoringUtil.TryArgMax(Enumerable.Range(0, teamScores.Length), i => teamScores[i], out int best)
                ? best
                : -1;

        /// <summary>
        /// Winner at match end. A single surviving team wins by forfeit regardless of score,
        /// otherwise falls back to <see cref="ResolveLeadingTeam"/>.
        /// </summary>
        protected int ResolveWinnerTeam()
        {
            var remaining = runningEvent.ParticipantsByGroupsStillInEvent;
            if (remaining.Count == 1)
                return runningEvent.TeamIndexOf(remaining[0].FirstOrDefault());
            return ResolveLeadingTeam();
        }

        /// <summary>
        /// Declares the team the winner (draw if the index is out of range) and flags the event
        /// to finish. Idempotent: callable from both the score-limit and time-up paths.
        /// </summary>
        protected void DeclareTeamWinner(int winnerTeam)
        {
            bool valid = winnerTeam >= 0 && winnerTeam < runningEvent.ParticipantsByGroupsBefore.Count;
            ResolveMatch(valid ? runningEvent.ParticipantsByGroupsBefore[winnerTeam] : null,
                "team score resolved");
        }

        /// <summary>
        /// Team placement by score (desc); equal scores share one bracket, so rewards can be
        /// paid per place to whole teams.
        /// </summary>
        public override List<HashSet<string>> GetFinalRanking()
        {
            var groups = runningEvent.ParticipantsByGroupsBefore;
            if (groups == null || groups.Count == 0) return null;

            return Enumerable.Range(0, groups.Count)
                .GroupBy(i => i < teamScores.Length ? teamScores[i] : 0)
                .OrderByDescending(g => g.Key)
                .Select(g =>
                {
                    var bracket = new HashSet<string>();
                    foreach (var i in g) bracket.UnionWith(groups[i]);
                    return bracket;
                })
                .ToList();
        }
    }
}
