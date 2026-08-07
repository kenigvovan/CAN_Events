using System;
using ProtoBuf;

namespace canevents.src.Network
{
    // Tournament traffic. House style: parallel flat arrays instead of nested DTOs, and deeply
    // nested config as a JSON string rather than a message shape (see HgSetupMsg for the precedent).

    // Ready check

    /// <summary>Server -> one player: your bracket match is up. The deadline is the server's; the
    /// seconds here are only what the client counts down on screen.</summary>
    [ProtoContract]
    public class TournamentReadyRequestMsg
    {
        [ProtoMember(1)] public long TournamentId;
        [ProtoMember(2)] public int MatchId;
        [ProtoMember(3)] public string TournamentName;
        [ProtoMember(4)] public string EventCode;
        [ProtoMember(5)] public string OpponentName;
        [ProtoMember(6)] public string RoundLabel;
        [ProtoMember(7)] public float TimeoutSeconds;
        [ProtoMember(8)] public bool AlreadyReady;   // set on a re-send after reconnect
    }

    /// <summary>Player -> server: accept the called match, or give it up.</summary>
    [ProtoContract]
    public class TournamentReadyResponseMsg
    {
        [ProtoMember(1)] public long TournamentId;
        [ProtoMember(2)] public int MatchId;
        [ProtoMember(3)] public bool Accept;
    }

    /// <summary>Server -> player: drop the popup (window closed, expired, opponent forfeited).</summary>
    [ProtoContract]
    public class TournamentReadyClosedMsg
    {
        [ProtoMember(1)] public long TournamentId;
        [ProtoMember(2)] public int MatchId;
        [ProtoMember(3)] public string Reason;
    }

    // Browsing

    [ProtoContract]
    public class RequestTournamentListMsg
    {
        /// <summary>Also send the archived ones - asked for only when the history is being looked at.</summary>
        [ProtoMember(1)] public bool IncludeHistory;
    }

    [ProtoContract]
    public class TournamentListMsg
    {
        [ProtoMember(1)] public long[] Ids;
        [ProtoMember(2)] public string[] Names;
        [ProtoMember(3)] public int[] States;                // TournamentState
        [ProtoMember(4)] public string[] EventCodesPacked;   // "1x1;ctf"
        [ProtoMember(5)] public int[] EntrantCounts;
        [ProtoMember(6)] public int[] MaxEntrants;
        [ProtoMember(7)] public string[] EntryFeePacked;     // ItemSetUtil.Pack
        [ProtoMember(8)] public string[] PotPacked;
        [ProtoMember(9)] public bool[] YouAreIn;
        [ProtoMember(10)] public long[] SignupDeadlineUnix;
        [ProtoMember(11)] public int[] TeamSizes;
        /// <summary>When a scheduled Draft opens its signup. 0 = opened by hand, nothing to show.</summary>
        [ProtoMember(12)] public long[] OpenAtUnix;

        // Read through these, never off the arrays directly: protobuf-net omits an array whose elements
        // are all default, so a field the server filled in can arrive null or short on the client.
        public int Count => Ids?.Length ?? 0;
        public string NameAt(int i) => At(Names, i) ?? "#" + At(Ids, i);
        public int StateAt(int i) => At(States, i);
        public string EventsAt(int i) => At(EventCodesPacked, i) ?? "";
        public int EntrantsAt(int i) => At(EntrantCounts, i);
        public int MaxEntrantsAt(int i) => At(MaxEntrants, i);
        public string FeeAt(int i) => At(EntryFeePacked, i) ?? "";
        public string PotAt(int i) => At(PotPacked, i) ?? "";
        public bool JoinedAt(int i) => At(YouAreIn, i);
        public long DeadlineAt(int i) => At(SignupDeadlineUnix, i);
        public int TeamSizeAt(int i) => Math.Max(1, At(TeamSizes, i));
        public long OpensAt(int i) => At(OpenAtUnix, i);

        private static T At<T>(T[] array, int i) => array != null && i >= 0 && i < array.Length ? array[i] : default;
    }

    [ProtoContract]
    public class RequestTournamentBracketMsg
    {
        [ProtoMember(1)] public long TournamentId;
    }

    /// <summary>One tournament's whole bracket; entrants and matches are parallel arrays and the
    /// client rebuilds the graph from the packed feed routes.</summary>
    [ProtoContract]
    public class TournamentBracketMsg
    {
        [ProtoMember(1)] public long TournamentId;
        [ProtoMember(2)] public string Name;
        [ProtoMember(3)] public int State;

        [ProtoMember(4)] public string[] EntrantIds;
        [ProtoMember(5)] public string[] EntrantNames;
        [ProtoMember(6)] public int[] EntrantSeeds;
        // Full roster of each side, for the detail line - the box only has room for the label.
        [ProtoMember(30)] public string[] EntrantMembers;

        [ProtoMember(7)] public int[] MatchIds;
        [ProtoMember(8)] public int[] MatchRounds;
        [ProtoMember(9)] public int[] MatchSlots;
        // One packed "id;id;id" per match - a node is not always a duel, so a fixed A/B pair cannot
        // describe a Hunger Games heat. An empty piece is an unfilled slot.
        [ProtoMember(10)] public string[] MatchSlotsPacked;
        [ProtoMember(12)] public string[] MatchWinner;
        [ProtoMember(13)] public int[] MatchStates;          // BracketMatchState
        // Where each advancing place goes: "matchId:slot;matchId:slot", one entry per place. A
        // knockout has one; a heat that sends two on has two, and they land in different nodes.
        [ProtoMember(14)] public string[] MatchFeedsPacked;
        [ProtoMember(15)] public int[] MatchAdvanceCount;
        [ProtoMember(16)] public bool[] MatchIsThirdPlace;
        [ProtoMember(17)] public string[] MatchEventCode;
        [ProtoMember(18)] public long[] MatchReadyDeadline;
        [ProtoMember(19)] public string[] MatchResultKind;
        // Sides that forfeited or never showed up; they keep their slot so a walkover still reads
        // as "X did not turn up" rather than a match against nobody.
        [ProtoMember(31)] public string[] MatchAbsentPacked;
        /// <summary>Classes allowed here; empty means the mode's full list. Shown to players.</summary>
        [ProtoMember(32)] public string[] AllowedLoadouts;

        [ProtoMember(20)] public string PotPacked;
        [ProtoMember(21)] public int[] PotSplitPercents;
        [ProtoMember(22)] public string[] PlacementPrizesPacked;   // one packed set per place
        [ProtoMember(23)] public string[] FinalPlacementEntrantIds;
        [ProtoMember(24)] public string EntryFeePacked;
        [ProtoMember(25)] public int RakePercent;
        // Needed to draw a preview bracket before the real one is built.
        [ProtoMember(26)] public bool ThirdPlaceMatch;
        [ProtoMember(27)] public int MaxEntrants;
        [ProtoMember(28)] public int PartiesPerMatch;   // sides per node: 2 for a duel bracket
        [ProtoMember(29)] public int AdvancePerMatch;  // how many of them go through

        // Scheduling, so the admin panel can load what is set instead of blanking it on save.
        [ProtoMember(33)] public long OpenAtUnix;
        [ProtoMember(34)] public int SignupWindowMinutes;
        [ProtoMember(35)] public int RepeatEveryMinutes;
        /// <summary>Daily repeat times as "HH:MM;HH:MM", empty when there are none.</summary>
        [ProtoMember(36)] public string RepeatAtPacked;
        /// <summary>Whether players see this draft in their list before signup opens.</summary>
        [ProtoMember(37)] public bool ShowBeforeSignup;
    }

    [ProtoContract]
    public class TournamentSignupMsg
    {
        [ProtoMember(1)] public long TournamentId;
        [ProtoMember(2)] public bool Join;         // false = withdraw
        /// <summary>Optional team name; empty falls back to "Leader +N". Ignored for solo brackets.</summary>
        [ProtoMember(3)] public string TeamName;
        /// <summary>Set to rename an existing entry rather than join.</summary>
        [ProtoMember(4)] public bool RenameOnly;
        /// <summary>Set to store a class choice for the whole tournament rather than join.</summary>
        [ProtoMember(5)] public bool LoadoutOnly;
        [ProtoMember(6)] public string LoadoutSet;
    }

    /// <summary>Every admin operation on one packet: ConfigJson carries a TournamentDraft for
    /// create/configure, the rest use the scalar fields. Answered via PlaceOperationResultMsg.</summary>
    [ProtoContract]
    public class TournamentAdminActionMsg
    {
        [ProtoMember(1)] public string Action;   // create|configure|opensignup|closesignup|begin|cancel|result|dq|forcecall
        [ProtoMember(2)] public long TournamentId;
        [ProtoMember(3)] public int MatchId;
        [ProtoMember(4)] public string StringArg;
        [ProtoMember(5)] public bool BoolArg;
        [ProtoMember(6)] public string ConfigJson;
    }
}
