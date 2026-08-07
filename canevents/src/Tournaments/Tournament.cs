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

    /// <summary>
    /// One side of the bracket: a player for a solo tournament, a whole party for a team one. The uid
    /// list is a snapshot taken at signup and deliberately survives party churn.
    /// </summary>
    public class Entrant
    {
        public string Id { get; set; }                       // guid, stable across restarts
        public List<string> Uids { get; set; } = new List<string>();

        /// <summary>
        /// What the bracket calls this side. Cached, not looked up, so a node still has a label when
        /// its players are offline. For a party: the team name, or "Leader +4".
        /// </summary>
        public string DisplayName { get; set; }

        /// <summary>The team's own name, when they set one. Empty means the default label is used.</summary>
        public string TeamName { get; set; }

        /// <summary>Every member, spelled out - for the detail line and tooltips, not the box.</summary>
        public string MembersText { get; set; }

        /// <summary>
        /// Class each member picked for the whole tournament (uid -> loadout set name), chosen once at
        /// signup instead of in every round's countdown. A set the round's event lacks is ignored.
        /// </summary>
        public Dictionary<string, string> LoadoutChoices { get; set; } = new Dictionary<string, string>();

        public int Seed { get; set; }                        // 1-based, assigned at Begin()
        public bool Eliminated { get; set; }
        public int PlacedAt { get; set; }                    // final placement, 0 until decided

        public bool Contains(string uid) => Uids != null && Uids.Contains(uid);
    }

    /// <summary>
    /// A node of the bracket. Winner/loser routing is explicit rather than derived from round
    /// geometry, so a losers' bracket can be added later without a data migration.
    /// </summary>
    public class BracketMatch
    {
        public int Id { get; set; }
        public int Round { get; set; }                       // 0 = first round
        public int SlotInRound { get; set; }

        /// <summary>
        /// The sides playing this match, one per slot; null/empty means TBD. A list rather than an A/B
        /// pair because a node is not always a duel - see <see cref="Arity"/>.
        /// </summary>
        public List<string> Slots { get; set; } = new List<string>();

        /// <summary>How many sides this match seats. 2 for a duel, more for a free-for-all mode.</summary>
        public int Arity { get; set; } = 2;

        /// <summary>
        /// How many sides come out of this match. 1 is a knockout; a heat can send its top few on,
        /// which is what makes big fields work without dozens of rounds.
        /// </summary>
        public int AdvanceCount { get; set; } = 1;

        /// <summary>
        /// Where each advancing place goes: Feeds[0] is the winner's seat, Feeds[1] the runner-up's.
        /// Empty in the final.
        /// </summary>
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

        /// <summary>
        /// Sides that were called and did not turn up. They stay in their slots on purpose: erasing
        /// the name would make a walkover indistinguishable from a slot that was never filled.
        /// </summary>
        public List<string> AbsentSides { get; set; } = new List<string>();

        /// <summary>Everyone who came out of this match, best first. Usually one name.</summary>
        public List<string> WinnerEntrantIds { get; set; } = new List<string>();

        /// <summary>The top place, for the many callers that only care who won.</summary>
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

        // Reserved for BO3/BO5: kept in the model so adding series support needs no data migration.
        public List<long> RunningEventIds { get; set; } = new List<long>();
        public int WinsA { get; set; }
        public int WinsB { get; set; }

        /// <summary>The slots that actually hold a side, in slot order.</summary>
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

        /// <summary>The sides that will actually play: everyone in a slot who has not dropped out.</summary>
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

        /// <summary>Ready to be played: every slot that will ever be filled is, and there are ≥2 sides.</summary>
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

    /// <summary>A seat in a later match that one advancing place feeds into.</summary>
    public class FeedTarget
    {
        public int MatchId { get; set; } = -1;
        public int Slot { get; set; }
    }

    /// <summary>One player's stake, kept so a withdrawal or a cancellation can hand back exactly what was taken.</summary>
    public class PotContribution
    {
        public string Uid { get; set; }
        public List<RewardItem> Items { get; set; }
    }

    /// <summary>A payout owed to someone who was offline when it was due. Delivered on reconnect.</summary>
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

        /// <summary>
        /// How many sides advance out of each match. 1 is a straight knockout; above 1 turns each node
        /// into a qualifying heat. Requires a mode that ranks its players, unlike a plain duel.
        /// </summary>
        public int AdvancePerMatch { get; set; } = 1;

        /// <summary>
        /// How many sides meet in one bracket match. 0 = derive it from the first event's party count
        /// (so a battle-royale mode runs real groups); 2 keeps the classic duel bracket.
        /// </summary>
        public int PartiesPerMatch { get; set; }
        public int ReadyCheckSeconds { get; set; } = 60;
        public int MaxConcurrentMatches { get; set; } = 1;

        /// <summary>
        /// Nothing is called until an admin says so, for events paced by hand (commentary between
        /// rounds, a stage to reset, a break).
        /// </summary>
        public bool ManualMatchStart { get; set; }
        public long SignupDeadlineUnix { get; set; }         // 0 = closed by hand only

        // scheduling

        /// <summary>A Draft opens its own signup at this moment. 0 = opened by hand.</summary>
        public long OpenAtUnix { get; set; }

        /// <summary>How long signup stays open, in minutes. A duration, not a moment, so a repeating
        /// tournament can re-derive its deadline every run. 0 = no window.</summary>
        public int SignupWindowMinutes { get; set; }

        /// <summary>Come back this many minutes after this one ENDS (so a long bracket never
        /// overlaps its successor). 0 = one-off.</summary>
        public int RepeatEveryMinutes { get; set; }

        /// <summary>Come back at these times of day (minutes from midnight, server local). Takes
        /// precedence over <see cref="RepeatEveryMinutes"/>.</summary>
        public List<int> RepeatAtMinutesOfDay { get; set; } = new List<int>();

        /// <summary>Whether this tournament comes back at all.</summary>
        public bool Repeats => RepeatEveryMinutes > 0
                            || (RepeatAtMinutesOfDay != null && RepeatAtMinutesOfDay.Count > 0);

        /// <summary>Whether players see this draft in their list before signup opens (an
        /// announcement). Admins always see everything.</summary>
        public bool ShowBeforeSignup { get; set; } = true;

        /// <summary>
        /// Start by itself once this many entrants have signed up. 0 = wait for an admin (or the
        /// deadline). Set it to MaxEntrants for the usual "start when full".
        /// </summary>
        public int AutoStartEntrants { get; set; }

        /// <summary>
        /// Grace period between reaching that count and starting, so the player who completed the
        /// field is not yanked straight into a match.
        /// </summary>
        public int AutoStartDelaySeconds { get; set; } = 15;

        /// <summary>When the countdown is running, the moment it fires. 0 = not armed.</summary>
        public long AutoStartAtUnix { get; set; }
        /// <summary>
        /// Whether the class picked at signup is fixed for the whole bracket. Locked stops
        /// counter-picking after the draw; unlocked lets people switch between rounds.
        /// </summary>
        public bool LockLoadout { get; set; }

        /// <summary>
        /// Which classes may be played, by loadout set name. Empty means every class the mode offers.
        /// </summary>
        public List<string> AllowedLoadouts { get; set; } = new List<string>();

        public bool AllowsLoadout(string setName)
            => AllowedLoadouts.Count == 0 || AllowedLoadouts.Contains(setName);

        /// <summary>
        /// Players fight with their own gear instead of an arena kit, and keep the risk: dying drops
        /// it and nothing is restored. See SavePlayerAttributes.KeepOwnGear.
        /// </summary>
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

    /// <summary>Root of tournaments.json.</summary>
    public class TournamentData
    {
        public long NextId { get; set; } = 1;
        public List<Tournament> Active { get; set; } = new List<Tournament>();
        public List<Tournament> History { get; set; } = new List<Tournament>();
        public List<PendingPayout> Pending { get; set; } = new List<PendingPayout>();
    }
}
