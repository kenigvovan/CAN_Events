using ProtoBuf;

namespace canevents.src.Network
{
    // Leaderboard messages

    [ProtoContract]
    public class RequestLeaderboardMsg
    {
        [ProtoMember(1)]
        public string EventCode; // null = all events combined
    }

    [ProtoContract]
    public class LeaderboardMsg
    {
        [ProtoMember(1)]
        public LeaderboardEntryMsg[] Entries;

        [ProtoMember(2)]
        public string[] AvailableEventCodes; // event types that have stats

        [ProtoMember(3)]
        public string FilteredEventCode; // which filter was applied (null = all)
    }

    [ProtoContract]
    public class LeaderboardEntryMsg
    {
        [ProtoMember(1)]
        public string PlayerName;

        [ProtoMember(2)]
        public int Wins;

        [ProtoMember(3)]
        public int Losses;

        [ProtoMember(4)]
        public int Kills;

        [ProtoMember(5)]
        public int Deaths;

        [ProtoMember(6)]
        public int EventsPlayed;

        [ProtoMember(7)]
        public float KDRatio;

        [ProtoMember(8)]
        public float WinRate;

        // Elo MMR for the filtered event code; default (1000) in the combined "all events" view,
        // where a single rating is meaningless.
        [ProtoMember(9)]
        public int Rating;
    }
}
