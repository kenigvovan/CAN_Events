using System.Collections.Generic;
using canevents.src.EventManagers.events;

namespace canevents.src.Tournaments
{
    public enum TournamentState
    {
        Draft,      // being configured, nobody can sign up yet
        Signup,     // open for entrants
        Seeded,     // signup closed, bracket built, not started
        Running,    // matches are being called
        Finished,
        Cancelled
    }

    public enum BracketMatchState
    {
        Pending,        // waiting to be called (may still be missing an entrant)
        AwaitingReady,  // both entrants notified, ready-check window is open
        Starting,       // RunningEvent created, arena search / countdown in progress
        Live,
        Done,
        Bye,            // never played: one side had no opponent
        Void            // both sides forfeited / draw - nobody advances
    }

    // A player or a whole party. Uids are a snapshot from signup and don't follow party changes.
    public class Entrant
    {
        public string Id { get; set; }                       // guid, stable across restarts
        public List<string> Uids { get; set; } = new List<string>();

        // Cached so the bracket has a label while players are offline. Team name or "Leader +4".
        public string DisplayName { get; set; }

        public string TeamName { get; set; }

        // Full member list for tooltips.
        public string MembersText { get; set; }

        // uid -> loadout set picked at signup. Sets the round's event lacks are ignored.
        public Dictionary<string, string> LoadoutChoices { get; set; } = new Dictionary<string, string>();

        public int Seed { get; set; }                        // 1-based, assigned at Begin()
        public bool Eliminated { get; set; }
        public int PlacedAt { get; set; }                    // final placement, 0 until decided

        public bool Contains(string uid) => Uids != null && Uids.Contains(uid);
    }

    // Winner/loser routing is stored explicitly, not derived from round geometry.
    public class BracketMatch
    {
        public int Id { get; set; }
        public int Round { get; set; }                       // 0 = first round
        public int SlotInRound { get; set; }

        // Entrant id per slot, null/empty = TBD.
        public List<string> Slots { get; set; } = new List<string>();

        // 2 for a duel, more for free-for-all.
        public int Arity { get; set; } = 2;

        // 1 = knockout, more = heat.
        public int AdvanceCount { get; set; } = 1;

        // Feeds[0] = winner's seat, Feeds[1] = runner-up's. Empty in the final.
        public List<FeedTarget> Feeds { get; set; } = new List<FeedTarget>();

        public int LoserFeedsMatchId { get; set; } = -1;     // only the semis of a duel bracket
        public int LoserFeedsSlot { get; set; }
        public bool IsThirdPlace { get; set; }
        public bool IsFinal { get; set; }

        public string EventCode { get; set; }                // resolved when the pair is called
        public BracketMatchState State { get; set; } = BracketMatchState.Pending;
        public long RunningEventId { get; set; }             // 0 when no match is live

        public long ReadyDeadlineUnix { get; set; }
        public List<string> ReadyUids { get; set; } = new List<string>();

        // Give up retrying a failed start at this time. 0 until the first failure.
        public long StartDeadlineUnix { get; set; }

        // No-shows. They keep their slot so a walkover differs from an empty slot.
        public List<string> AbsentSides { get; set; } = new List<string>();

        // Best first.
        public List<string> WinnerEntrantIds { get; set; } = new List<string>();

        public string WinnerEntrantId
        {
            get => WinnerEntrantIds.Count > 0 ? WinnerEntrantIds[0] : null;
            set
            {
                WinnerEntrantIds.Clear();
                if (!string.IsNullOrEmpty(value)) WinnerEntrantIds.Add(value);
            }
        }

        public string LoserEntrantId { get; set; }
        public string ResultKind { get; set; }               // played|bye|technical|doubleforfeit|admin|void
        public long FinishedUnix { get; set; }

        public List<string> FilledSlots
        {
            get
            {
                var filled = new List<string>();
                foreach (var s in Slots) if (!string.IsNullOrEmpty(s)) filled.Add(s);
                return filled;
            }
        }

        public int FilledCount => FilledSlots.Count;

        // Filled slots minus absent sides.
        public List<string> ActiveSides
        {
            get
            {
                var active = new List<string>();
                foreach (var s in FilledSlots) if (!AbsentSides.Contains(s)) active.Add(s);
                return active;
            }
        }

        public bool IsAbsent(string entrantId) => AbsentSides.Contains(entrantId);

        // Full test is BracketBuilder.ReadyToPlay.
        public bool IsPlayable => FilledCount >= 2 && FilledCount == Slots.Count;

        public bool Contains(string entrantId)
            => !string.IsNullOrEmpty(entrantId) && Slots.Contains(entrantId);

        public void SetSlot(int index, string entrantId)
        {
            while (Slots.Count <= index) Slots.Add(null);
            Slots[index] = entrantId;
        }

        public bool IsClosed => State == BracketMatchState.Done
                             || State == BracketMatchState.Bye
                             || State == BracketMatchState.Void;
    }

    public class FeedTarget
    {
        public int MatchId { get; set; } = -1;
        public int Slot { get; set; }
    }

    // Kept so withdrawal/cancel refunds exactly what was taken.
    public class PotContribution
    {
        public string Uid { get; set; }
        public List<RewardItem> Items { get; set; }
    }

    // Owed to an offline player, delivered on reconnect.
    public class PendingPayout
    {
        public string Uid { get; set; }
        public List<RewardItem> Items { get; set; }
        public long TournamentId { get; set; }
        public long QueuedUnix { get; set; }
        public string Reason { get; set; }                   // prize|refund
    }

    public class Tournament
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public TournamentState State { get; set; } = TournamentState.Draft;

        // admin configuration
        public List<string> EventCodes { get; set; } = new List<string>();
        public List<long> PlaceWhitelist { get; set; } = new List<long>();   // empty = any suitable arena
        public int TeamSize { get; set; } = 1;
        public int MaxEntrants { get; set; } = 32;

        public List<RewardItem> EntryFee { get; set; } = new List<RewardItem>();
        public int RakePercent { get; set; }
        public List<int> PotSplitPercents { get; set; } = new List<int> { 60, 30, 10 };
        public List<RewardTier> PlacementPrizes { get; set; } = new List<RewardTier>();

        public bool ThirdPlaceMatch { get; set; } = true;

        // >1 makes each node a heat; needs a mode that ranks players.
        public int AdvancePerMatch { get; set; } = 1;

        // Sides per match. 0 = take the first event's party count.
        public int PartiesPerMatch { get; set; }
        public int ReadyCheckSeconds { get; set; } = 60;
        public int MaxConcurrentMatches { get; set; } = 1;

        // Matches are only called by an admin.
        public bool ManualMatchStart { get; set; }
        public long SignupDeadlineUnix { get; set; }         // 0 = closed by hand only

        // scheduling

        // Draft opens signup at this time. 0 = by hand.
        public long OpenAtUnix { get; set; }

        // Minutes, so repeats can recompute the deadline. 0 = no window.
        public int SignupWindowMinutes { get; set; }

        // Minutes after the previous run ENDS. 0 = one-off.
        public int RepeatEveryMinutes { get; set; }

        // Minutes from midnight, server local. Overrides RepeatEveryMinutes.
        public List<int> RepeatAtMinutesOfDay { get; set; } = new List<int>();

        public bool Repeats => RepeatEveryMinutes > 0
                            || (RepeatAtMinutesOfDay != null && RepeatAtMinutesOfDay.Count > 0);

        // Players see the draft before signup opens. Admins always do.
        public bool ShowBeforeSignup { get; set; } = true;

        // Auto start at this many entrants. 0 = admin or deadline.
        public int AutoStartEntrants { get; set; }

        public int AutoStartDelaySeconds { get; set; } = 15;

        // 0 = not armed.
        public long AutoStartAtUnix { get; set; }
        // Class picked at signup can't be changed between rounds.
        public bool LockLoadout { get; set; }

        // Empty = all classes of the mode.
        public List<string> AllowedLoadouts { get; set; } = new List<string>();

        public bool AllowsLoadout(string setName)
            => AllowedLoadouts.Count == 0 || AllowedLoadouts.Contains(setName);

        // Own gear, dropped on death and not restored. See SavePlayerAttributes.KeepOwnGear.
        public bool BringOwnGear { get; set; }

        public int SeedMode { get; set; }                    // 0 = random, 1 = by Elo
        public int RatedMode { get; set; }                   // 0 = inherit the event, 1 = force unrated
        public bool PauseScheduler { get; set; } = true;     // suspend the auto-scheduler while running
        public bool LockEntrantsWhileRunning { get; set; }   // false: entrants may play casual between rounds

        // live state
        public List<Entrant> Entrants { get; set; } = new List<Entrant>();
        public List<BracketMatch> Matches { get; set; } = new List<BracketMatch>();
        public Dictionary<string, int> Pot { get; set; } = new Dictionary<string, int>();
        public List<PotContribution> Contributions { get; set; } = new List<PotContribution>();
        public List<string> FinalPlacement { get; set; } = new List<string>();  // entrant ids, [0] = 1st

        public long CreatedUnix { get; set; }
        public long StartedUnix { get; set; }
        public long FinishedUnix { get; set; }

        public Entrant FindEntrant(string entrantId)
        {
            if (string.IsNullOrEmpty(entrantId)) return null;
            foreach (var e in Entrants) if (e.Id == entrantId) return e;
            return null;
        }

        public Entrant EntrantOfPlayer(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return null;
            foreach (var e in Entrants) if (e.Contains(uid)) return e;
            return null;
        }

        public BracketMatch FindMatch(int matchId)
        {
            foreach (var m in Matches) if (m.Id == matchId) return m;
            return null;
        }

        public string NameOfEntrant(string entrantId) => FindEntrant(entrantId)?.DisplayName ?? "TBD";
    }

    // Root of tournaments.json.
    public class TournamentData
    {
        public long NextId { get; set; } = 1;
        public List<Tournament> Active { get; set; } = new List<Tournament>();
        public List<Tournament> History { get; set; } = new List<Tournament>();
        public List<PendingPayout> Pending { get; set; } = new List<PendingPayout>();
    }
}
