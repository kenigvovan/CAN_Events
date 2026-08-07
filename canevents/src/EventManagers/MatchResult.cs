using System.Collections.Generic;
using System.Linq;

namespace canevents.src.EventManagers
{
    // Immutable snapshot of a finished match, frozen BEFORE behavior cleanup (OnEnd) runs, so teardown
    // can't wipe the state that rewards and stats read afterwards. Reading the outcome must happen
    // before OnEnd but applying rewards after it, and this frozen result bridges the two.
    public sealed class MatchResult
    {
        public HashSet<string> Winners { get; }
        // Ordered placement brackets, best first; null when the match can't be ranked.
        public List<HashSet<string>> Ranking { get; }

        private readonly Dictionary<string, int> kills;
        private readonly Dictionary<string, int> deaths;

        public MatchResult(HashSet<string> winners, List<HashSet<string>> ranking,
            Dictionary<string, int> kills, Dictionary<string, int> deaths)
        {
            // Defensive copies: the scorer hands back LIVE collections (GetWinners() may return the
            // live ParticipantsByGroupsStillInEvent[0]), and aliasing them would let post-capture
            // mutation silently corrupt the snapshot.
            Winners = winners != null ? new HashSet<string>(winners) : new HashSet<string>();
            Ranking = ranking?.Select(b => new HashSet<string>(b)).ToList();
            this.kills = kills != null ? new Dictionary<string, int>(kills) : null;
            this.deaths = deaths != null ? new Dictionary<string, int>(deaths) : null;
        }

        public int KillsOf(string uid) => kills != null && kills.TryGetValue(uid, out var k) ? k : 0;
        public int DeathsOf(string uid) => deaths != null && deaths.TryGetValue(uid, out var d) ? d : 0;
        public bool Won(string uid) => Winners.Contains(uid);
    }
}
