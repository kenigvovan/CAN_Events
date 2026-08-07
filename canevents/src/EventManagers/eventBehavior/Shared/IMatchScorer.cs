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
    }
}
