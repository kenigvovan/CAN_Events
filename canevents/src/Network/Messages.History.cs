using ProtoBuf;

namespace canevents.src.Network
{
    // Match history messages

    [ProtoContract]
    public class RequestMatchHistoryMsg { }

    [ProtoContract]
    public class MatchHistoryMsg
    {
        [ProtoMember(1)]
        public MatchHistoryEntryMsg[] Entries;
    }

    [ProtoContract]
    public class MatchHistoryEntryMsg
    {
        [ProtoMember(1)] public string EventCode;
        [ProtoMember(2)] public long TimestampUnix;
        [ProtoMember(3)] public string[] WinnerNames;
        [ProtoMember(4)] public MatchHistoryScoreMsg[] Scores;
        [ProtoMember(5)] public int DurationSeconds;
    }

    [ProtoContract]
    public class MatchHistoryScoreMsg
    {
        [ProtoMember(1)] public string PlayerName;
        [ProtoMember(2)] public int Kills;
        [ProtoMember(3)] public int Deaths;
        [ProtoMember(4)] public bool Won;
    }
}
