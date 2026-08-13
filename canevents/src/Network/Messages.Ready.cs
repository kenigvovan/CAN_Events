using ProtoBuf;

namespace canevents.src.Network
{
    // Ready check for queue matches. Kept apart from the tournament one (TournamentReadyRequestMsg)
    // because the stakes differ and so does the wording: a tournament no-show forfeits the match,
    // a queue no-show is simply left out of a match that then starts without them.

    /// <summary>Server -> player: your queued match assembled, confirm you are here.</summary>
    [ProtoContract]
    public class MatchReadyRequestMsg
    {
        [ProtoMember(1)] public long EventId;
        [ProtoMember(2)] public string EventCode;
        [ProtoMember(3)] public float TimeoutSeconds;

        /// <summary>Players in the whole line-up, so the prompt can say what it is you are joining.</summary>
        [ProtoMember(4)] public int TotalPlayers;
    }

    /// <summary>Player -> server: yes or no.</summary>
    [ProtoContract]
    public class MatchReadyResponseMsg
    {
        [ProtoMember(1)] public long EventId;
        [ProtoMember(2)] public bool Ready;
    }

    /// <summary>Server -> player: take the prompt down (answered, closed, cancelled).</summary>
    [ProtoContract]
    public class MatchReadyClosedMsg
    {
        [ProtoMember(1)] public long EventId;
    }
}
