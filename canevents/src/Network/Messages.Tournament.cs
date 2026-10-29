using System;
using ProtoBuf;

namespace canevents.src.Network
{
    // TimeoutSeconds is display only, the server owns the deadline.
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

    [ProtoContract]
    public class TournamentReadyResponseMsg
    {
        [ProtoMember(1)] public long TournamentId;
        [ProtoMember(2)] public int MatchId;
        [ProtoMember(3)] public bool Accept;
    }

    [ProtoContract]
    public class TournamentReadyClosedMsg
    {
        [ProtoMember(1)] public long TournamentId;
        [ProtoMember(2)] public int MatchId;
        [ProtoMember(3)] public string Reason;
    }

    [ProtoContract]
    public class RequestTournamentListMsg
    {
        // Include archived tournaments.
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
        // 0 = opened by hand.
        [ProtoMember(12)] public long[] OpenAtUnix;

        // Use these accessors: protobuf-net drops all-default arrays, so they can arrive null.
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

    // Client rebuilds the bracket graph from MatchFeedsPacked.
    [ProtoContract]
    public class TournamentBracketMsg
    {
        [ProtoMember(1)] public long TournamentId;
        [ProtoMember(2)] public string Name;
        [ProtoMember(3)] public int State;

        [ProtoMember(4)] public string[] EntrantIds;
        [ProtoMember(5)] public string[] EntrantNames;
        [ProtoMember(6)] public int[] EntrantSeeds;
        // Full roster per side.
        [ProtoMember(30)] public string[] EntrantMembers;

        [ProtoMember(7)] public int[] MatchIds;
        [ProtoMember(8)] public int[] MatchRounds;
        [ProtoMember(9)] public int[] MatchSlots;
        // "id;id;id" per match, since a heat can have more than two sides. Empty = unfilled slot.
        [ProtoMember(10)] public string[] MatchSlotsPacked;
        [ProtoMember(12)] public string[] MatchWinner;
        [ProtoMember(13)] public int[] MatchStates;          // BracketMatchState
        // "matchId:slot;matchId:slot", one entry per advancing place.
        [ProtoMember(14)] public string[] MatchFeedsPacked;
        [ProtoMember(15)] public int[] MatchAdvanceCount;
        [ProtoMember(16)] public bool[] MatchIsThirdPlace;
        [ProtoMember(17)] public string[] MatchEventCode;
        [ProtoMember(18)] public long[] MatchReadyDeadline;
        [ProtoMember(19)] public string[] MatchResultKind;
        // Sides that forfeited or didn't show; they keep their slot.
        [ProtoMember(31)] public string[] MatchAbsentPacked;
        // Empty = the mode's full list.
        [ProtoMember(32)] public string[] AllowedLoadouts;

        [ProtoMember(20)] public string PotPacked;
        [ProtoMember(21)] public int[] PotSplitPercents;
        [ProtoMember(22)] public string[] PlacementPrizesPacked;   // one packed set per place
        [ProtoMember(23)] public string[] FinalPlacementEntrantIds;
        [ProtoMember(24)] public string EntryFeePacked;
        [ProtoMember(25)] public int RakePercent;
        // For the preview bracket before the real one exists.
        [ProtoMember(26)] public bool ThirdPlaceMatch;
        [ProtoMember(27)] public int MaxEntrants;
        [ProtoMember(28)] public int PartiesPerMatch;   // sides per node: 2 for a duel bracket
        [ProtoMember(29)] public int AdvancePerMatch;  // how many of them go through

        [ProtoMember(33)] public long OpenAtUnix;
        [ProtoMember(34)] public int SignupWindowMinutes;
        [ProtoMember(35)] public int RepeatEveryMinutes;
        // "HH:MM;HH:MM", empty = none.
        [ProtoMember(36)] public string RepeatAtPacked;
        [ProtoMember(37)] public bool ShowBeforeSignup;

        // Fields below are only filled for admins.
        [ProtoMember(38)] public bool HasAdminSettings;
        // Empty = any suitable arena.
        [ProtoMember(39)] public long[] PlaceWhitelist;
        [ProtoMember(40)] public bool LockLoadout;
        [ProtoMember(41)] public bool BringOwnGear;
        [ProtoMember(42)] public bool ManualMatchStart;
        [ProtoMember(43)] public int ReadyCheckSeconds;
        [ProtoMember(44)] public int MaxConcurrentMatches;
        [ProtoMember(45)] public int AutoStartEntrants;      // 0 = off
        [ProtoMember(46)] public int AutoStartDelaySeconds;
        [ProtoMember(47)] public int SeedMode;               // 0 = random, 1 = by Elo
        [ProtoMember(48)] public int RatedMode;              // 0 = inherit the event, 1 = force unrated
        [ProtoMember(49)] public bool PauseScheduler;
    }

    [ProtoContract]
    public class TournamentSignupMsg
    {
        [ProtoMember(1)] public long TournamentId;
        [ProtoMember(2)] public bool Join;         // false = withdraw
        // Empty = "Leader +N". Ignored for solo brackets.
        [ProtoMember(3)] public string TeamName;
    }

    [ProtoContract]
    public class TournamentRenameMsg
    {
        [ProtoMember(1)] public long TournamentId;
        [ProtoMember(2)] public string TeamName;
    }

    // Class used for the whole tournament.
    [ProtoContract]
    public class TournamentLoadoutMsg
    {
        [ProtoMember(1)] public long TournamentId;
        [ProtoMember(2)] public string SetName;
    }

    // ConfigJson is a TournamentDraft for create/configure.
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
