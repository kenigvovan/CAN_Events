using System.Collections.Generic;

namespace canevents.src.EventManagers
{
    /// <summary>
    /// Who is playing this match, and who is still in it. The "...Before" pair is the starting line-up
    /// and never changes after <see cref="Initialize"/>; the "...StillInEvent" pair shrinks as players
    /// drop out, and its groups MUST stay a deep copy - sharing the HashSets would let an elimination
    /// silently empty the starting snapshot that rewards, Elo and the final ranking are computed from.
    ///
    /// The starting line-up is a list because its order is meaningful (spawn points are handed out by
    /// position); the active set is a HashSet, making <see cref="IsActive"/> O(1).
    /// </summary>
    public class MatchRoster
    {
        private readonly List<string> participantsBefore = new();
        private readonly List<HashSet<string>> participantsByGroupsBefore = new();
        private readonly HashSet<string> participantsStillInEvent = new();
        private readonly List<HashSet<string>> participantsByGroupsStillInEvent = new();

        public IReadOnlyList<string> ParticipantsBefore => participantsBefore;
        public IReadOnlyList<HashSet<string>> ParticipantsByGroupsBefore => participantsByGroupsBefore;
        public IReadOnlyCollection<string> ParticipantsStillInEvent => participantsStillInEvent;
        public IReadOnlyList<HashSet<string>> ParticipantsByGroupsStillInEvent => participantsByGroupsStillInEvent;

        // Players who went offline during SearchingForLocation. Excluded at Initialize() so they can't
        // be handed a spawn, win while offline, or stop the online side from ending the match. They
        // stay in participantsBefore (the reward/Elo snapshot) but never enter play.
        private readonly HashSet<string> preStartDropouts = new();

        // When set, GetWinners() returns this verbatim instead of the single-remaining-group fallback.
        // Lets team-based EBs pick a winner without mutating the active groups.
        private HashSet<string> explicitWinners = null;

        // uid → team index. Built on first use and kept, because ParticipantsByGroupsBefore is a
        // pre-match snapshot that never changes (eliminations mutate the ...StillInEvent lists).
        private Dictionary<string, int> teamIndexByUid;
        private bool teamIndexBuilt;

        /// <summary>Records the starting line-up. Called once, while the match is being assembled.</summary>
        public void SetStartingLineup(IEnumerable<string> allUids, IEnumerable<HashSet<string>> groups)
        {
            if (groups != null) participantsByGroupsBefore.AddRange(groups);
            if (allUids != null) participantsBefore.AddRange(allUids);
        }

        /// <summary>Marks a participant who disconnected before the match started, so
        /// <see cref="Initialize"/> leaves them out. No-op once running - use
        /// <see cref="RemoveFromPlay"/> then.</summary>
        public void MarkPreStartDropout(string uid)
        {
            if (!string.IsNullOrEmpty(uid)) preStartDropouts.Add(uid);
        }

        /// <summary>Has this player already dropped out before the start? Asked by the ready check,
        /// which must tell "declined" apart from "has not answered yet".</summary>
        public bool IsPreStartDropout(string uid)
            => !string.IsNullOrEmpty(uid) && preStartDropouts.Contains(uid);

        /// <summary>
        /// Copies the starting line-up into the active one when the match starts. Groups are
        /// deep-copied (see the invariant above); pre-start dropouts are skipped and any group left
        /// empty is dropped, so the online side isn't stuck waiting on a ghost.
        /// </summary>
        public void Initialize()
        {
            participantsStillInEvent.Clear();
            foreach (var uid in participantsBefore)
                if (!preStartDropouts.Contains(uid)) participantsStillInEvent.Add(uid);

            participantsByGroupsStillInEvent.Clear();
            foreach (var group in participantsByGroupsBefore)
            {
                var live = new HashSet<string>(group);
                live.RemoveWhere(uid => preStartDropouts.Contains(uid));
                if (live.Count > 0) participantsByGroupsStillInEvent.Add(live);
            }
        }

        /// <summary>Position of a player in the starting line-up, or -1. Used for spawn assignment.</summary>
        public int IndexInStartingLineup(string uid) => participantsBefore.IndexOf(uid);

        /// <summary>Is this player still in play (not eliminated, not finished)?</summary>
        // Goes to the HashSet directly: through the IReadOnlyCollection property this would bind to
        // Enumerable.Contains and walk the whole set.
        public bool IsActive(string uid) => uid != null && participantsStillInEvent.Contains(uid);

        /// <summary>Did this player start the match? Stays true after elimination: this is the set that
        /// gets rewards, inventory restores and end-of-match messages.</summary>
        public bool IsParticipant(string uid) => uid != null && participantsBefore.Contains(uid);

        /// <summary>The team this player started on, or -1 when they are not a participant. Index into
        /// <see cref="ParticipantsByGroupsBefore"/>, the same numbering spawn points use.</summary>
        public int TeamIndexOf(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return -1;

            // Built once, on the first lookup that finds a non-empty roster. Keyed on a flag rather
            // than emptiness, or a legitimately empty match would rebuild on every call.
            if (!teamIndexBuilt && participantsByGroupsBefore.Count > 0)
            {
                teamIndexByUid = new Dictionary<string, int>();
                for (int i = 0; i < participantsByGroupsBefore.Count; i++)
                    foreach (var memberUid in participantsByGroupsBefore[i])
                        teamIndexByUid[memberUid] = i;

                teamIndexBuilt = true;
            }

            return teamIndexByUid != null && teamIndexByUid.TryGetValue(uid, out var index) ? index : -1;
        }

        /// <summary>Takes a player out of play. Says nothing about whether the match is over - that
        /// rule is the match's (see <see cref="RunningEvent.CheckLastGroupStanding"/>), since respawn
        /// modes remove players from play without deciding anything.</summary>
        public void RemoveFromPlay(string playerUid)
        {
            participantsStillInEvent.Remove(playerUid);

            foreach (var group in participantsByGroupsStillInEvent)
            {
                group.Remove(playerUid);
            }
            participantsByGroupsStillInEvent.RemoveAll(g => g.Count == 0);
        }

        /// <summary>Declares the winning UIDs; null/empty means "draw". Takes precedence over the
        /// single-remaining-group fallback in <see cref="GetWinners"/>.</summary>
        public void SetWinners(IEnumerable<string> winners)
        {
            explicitWinners = winners != null ? new HashSet<string>(winners) : new HashSet<string>();
        }

        /// <summary>The winning group's UIDs, or empty. Prefers explicit winners, else falls back to
        /// the single remaining group (last team standing).</summary>
        public HashSet<string> GetWinners()
        {
            if (explicitWinners != null) return explicitWinners;
            if (participantsByGroupsStillInEvent.Count == 1)
            {
                return participantsByGroupsStillInEvent[0];
            }
            return new HashSet<string>();
        }
    }
}
