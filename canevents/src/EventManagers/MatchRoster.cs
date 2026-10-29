using System.Collections.Generic;
using System.Linq;

namespace canevents.src.EventManagers
{
    // "Before" = starting line-up, fixed except in open matches and lobby regrouping. "StillInEvent" groups must be deep copies, or eliminations
    // empty the snapshot rewards and Elo are computed from. Line-up order = spawn order.
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

        // Left before the start: kept in participantsBefore for rewards/Elo, never enter play.
        private readonly HashSet<string> preStartDropouts = new();

        // Overrides the last-group fallback in GetWinners.
        private HashSet<string> explicitWinners = null;

        // Rebuilt when Admit/Withdraw/Regroup change the "Before" groups.
        private Dictionary<string, int> teamIndexByUid;
        private bool teamIndexBuilt;

        public void SetStartingLineup(IEnumerable<string> allUids, IEnumerable<HashSet<string>> groups)
        {
            if (groups != null) participantsByGroupsBefore.AddRange(groups);
            if (allUids != null) participantsBefore.AddRange(allUids);
        }

        // No-op once running, use RemoveFromPlay then.
        public void MarkPreStartDropout(string uid)
        {
            if (!string.IsNullOrEmpty(uid)) preStartDropouts.Add(uid);
        }

        public bool IsPreStartDropout(string uid)
            => !string.IsNullOrEmpty(uid) && preStartDropouts.Contains(uid);

        // Deep copy, minus dropouts; empty groups are dropped.
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

        public int IndexInStartingLineup(string uid) => participantsBefore.IndexOf(uid);

        // Field, not the IReadOnlyCollection property, which would bind to Enumerable.Contains.
        public bool IsActive(string uid) => uid != null && participantsStillInEvent.Contains(uid);

        // Stays true after elimination.
        public bool IsParticipant(string uid) => uid != null && participantsBefore.Contains(uid);

        public int TeamIndexOf(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return -1;

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

        // Doesn't decide the match, see RunningEvent.CheckLastGroupStanding.
        public void RemoveFromPlay(string playerUid)
        {
            participantsStillInEvent.Remove(playerUid);

            foreach (var group in participantsByGroupsStillInEvent)
            {
                group.Remove(playerUid);
            }
            participantsByGroupsStillInEvent.RemoveAll(g => g.Count == 0);
        }

        // Open matches and rounds only: rated results assume the line-up never changes.
        public bool Admit(string uid, int teamIdx)
        {
            if (string.IsNullOrEmpty(uid) || teamIdx < 0 || teamIdx >= participantsByGroupsBefore.Count) return false;
            if (participantsBefore.Contains(uid)) return false;

            participantsBefore.Add(uid);
            participantsByGroupsBefore[teamIdx].Add(uid);
            preStartDropouts.Remove(uid);
            teamIndexBuilt = false;
            ReturnToPlay(uid);
            return true;
        }

        // Off the roster entirely, so busy checks let go of them.
        public void Withdraw(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return;
            RemoveFromPlay(uid);
            participantsBefore.Remove(uid);
            foreach (var group in participantsByGroupsBefore) group.Remove(uid);
            teamIndexBuilt = false;
        }

        // Back into its own live group; the live list drops empty groups, so it may have to come back.
        public void ReturnToPlay(string uid)
        {
            int team = TeamIndexOf(uid);
            if (team < 0) return;

            participantsStillInEvent.Add(uid);
            foreach (var live in participantsByGroupsStillInEvent)
            {
                if (live.Count > 0 && TeamIndexOf(live.First()) == team)
                {
                    live.Add(uid);
                    return;
                }
            }
            participantsByGroupsStillInEvent.Add(new HashSet<string> { uid });
        }

        // Pre-start only. Team count stays the same; everyone must land in exactly one group.
        // Who is still in play doesn't change, only which team they are on.
        public void Regroup(IReadOnlyList<HashSet<string>> groups)
        {
            if (groups == null || groups.Count != participantsByGroupsBefore.Count) return;

            for (int i = 0; i < groups.Count; i++)
                participantsByGroupsBefore[i] = new HashSet<string>(groups[i]);

            participantsBefore.Clear();
            foreach (var group in participantsByGroupsBefore) participantsBefore.AddRange(group);
            teamIndexBuilt = false;

            participantsByGroupsStillInEvent.Clear();
            foreach (var group in participantsByGroupsBefore)
            {
                var live = new HashSet<string>(group.Where(participantsStillInEvent.Contains));
                if (live.Count > 0) participantsByGroupsStillInEvent.Add(live);
            }
        }

        public int CountInTeam(int teamIdx)
            => teamIdx >= 0 && teamIdx < participantsByGroupsBefore.Count ? participantsByGroupsBefore[teamIdx].Count : 0;

        // null/empty = draw.
        public void SetWinners(IEnumerable<string> winners)
        {
            explicitWinners = winners != null ? new HashSet<string>(winners) : new HashSet<string>();
        }

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
