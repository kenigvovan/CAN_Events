using System.Collections.Generic;
using canevents.src.EventManagers.events;

namespace canevents.src.Duels
{
    // No rated kind: players pick their own opponents, so duels could be used to farm Elo.
    public enum DuelKind
    {
        Friendly = 0,

        // Both sides stake the same items, winner takes all.
        Wager = 1
    }

    // Sides are captured at creation and rechecked on accept.
    public class DuelChallenge
    {
        public long Id;

        public string ChallengerUid;
        public string TargetUid;

        // Leader first: the leader pays and gets refunds for a wager.
        public List<string> ChallengerSide = new List<string>();

        public List<string> TargetSide = new List<string>();

        public DuelKind Kind = DuelKind.Friendly;

        public List<RewardItem> Wager;

        // World.ElapsedMilliseconds
        public long ExpiresAtMs;

        public string EventCode;

        // Challenger chose the mode; if it's gone on accept the duel is cancelled.
        public bool ModePinned;

        // Wire form, e.g. "noarmor", "health:0.5".
        public List<string> Mutators = new List<string>();

        public int SideSize => ChallengerSide.Count;

        public bool Involves(string uid)
            => ChallengerSide.Contains(uid) || TargetSide.Contains(uid);
    }
}
