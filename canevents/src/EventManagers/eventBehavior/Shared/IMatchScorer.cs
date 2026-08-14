using System.Collections.Generic;

namespace canevents.src.EventManagers.eventBehavior.Shared
{
    // Implemented by the one mode behavior that owns a match's scoring; RunningEvent resolves
    // exactly one per match (RunningEvent.Scorer). Kept off CANEventBehavior so non-scoring
    // behaviors don't carry empty score hooks.
    public interface IMatchScorer
    {
        // Placement brackets, best first; one player (FFA) or a team per bracket, ties share one.
        // Null when this scorer can't produce a ranking.
        List<HashSet<string>> GetFinalRanking();

        // Per-player kill / death tallies, or null if this mode doesn't track them.
        Dictionary<string, int> GetFinalKillCounts();
        Dictionary<string, int> GetFinalDeathCounts();

        /// <summary>
        /// Blanks the per-player kills/deaths in the live scoreboard. For hidden-role modes, where
        /// a kill count is a role: the first player with a kill is the murderer. Honored in
        /// ServerNetworkHandler.BuildScoreboard, which every scoreboard packet goes through.
        /// The end-of-match results table is unaffected - by then the roles are public.
        /// </summary>
        bool HidesIndividualScores => false;

        /// <summary>
        /// Nobody is named as a killer in the kill feed or the death announcements. Asked by every
        /// path that announces a death - including the bot one, which builds its own message and
        /// would otherwise name the murderer the first time they cut down a bot.
        /// </summary>
        bool HidesKillerIdentity => false;
    }
}
