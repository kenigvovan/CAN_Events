using System.Collections.Generic;
using canevents.src.EventManagers.events;

namespace canevents.src.Duels
{
    /// <summary>
    /// What is at stake in a duel.
    ///
    /// There is deliberately no rated kind. A duel's line-up is chosen by the players themselves, so
    /// letting it move Elo would let two of them trade wins until the ladder means nothing - and a
    /// rating cannot be un-inflated after the fact. Rating stays with the queue and with tournaments,
    /// where nobody picks their own opponent.
    /// </summary>
    public enum DuelKind
    {
        /// <summary>Nothing changes hands. Just the match.</summary>
        Friendly = 0,

        /// <summary>Both sides put up the same items; the winner takes the lot.</summary>
        Wager = 1
    }

    /// <summary>
    /// One outstanding challenge, from the moment it is sent until it is accepted, declined or times
    /// out. Sides are captured when the challenge is CREATED, not when it is accepted: a party that
    /// changes shape in between must not silently alter the terms the other side agreed to - the
    /// mismatch is caught at accept time and the challenge is dropped instead.
    /// </summary>
    public class DuelChallenge
    {
        public long Id;

        public string ChallengerUid;
        public string TargetUid;

        /// <summary>Everyone on the challenging side, leader first. The leader is who pays a wager
        /// and who gets it refunded, so the order matters.</summary>
        public List<string> ChallengerSide = new List<string>();

        /// <summary>The same for the challenged side.</summary>
        public List<string> TargetSide = new List<string>();

        public DuelKind Kind = DuelKind.Friendly;

        /// <summary>What each side stakes. Only meaningful for <see cref="DuelKind.Wager"/>.</summary>
        public List<RewardItem> Wager;

        /// <summary>Server clock (World.ElapsedMilliseconds) at which this challenge lapses.</summary>
        public long ExpiresAtMs;

        /// <summary>Event this will be played as, resolved when the challenge is created so the
        /// prompt can name it. Re-resolved at accept time - the mode could be disabled in between.</summary>
        public string EventCode;

        /// <summary>How many players per side. Both sides always match - a challenge with unequal
        /// sides is refused when it is created.</summary>
        public int SideSize => ChallengerSide.Count;

        /// <summary>Is this uid involved on either side? Used to refuse a second challenge that
        /// overlaps one already outstanding.</summary>
        public bool Involves(string uid)
            => ChallengerSide.Contains(uid) || TargetSide.Contains(uid);
    }
}
