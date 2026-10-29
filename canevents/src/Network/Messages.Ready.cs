using ProtoBuf;

namespace canevents.src.Network
{
    // Ready check for queue matches. Kept apart from the tournament one (TournamentReadyRequestMsg)
    // because the stakes differ and so does the wording: a tournament no-show forfeits the match,
    // a queue no-show is simply left out of a match that then starts without them.

    // Server -> player: your queued match assembled, confirm you are here.
    [ProtoContract]
    public class MatchReadyRequestMsg
    {
        [ProtoMember(1)] public long EventId;
        [ProtoMember(2)] public string EventCode;
        [ProtoMember(3)] public float TimeoutSeconds;

        // Whole line-up, so the prompt can say what you are joining.
        [ProtoMember(4)] public int TotalPlayers;
    }

    // Player -> server: yes or no.
    [ProtoContract]
    public class MatchReadyResponseMsg
    {
        [ProtoMember(1)] public long EventId;
        [ProtoMember(2)] public bool Ready;
    }

    // Server -> player: take the prompt down (answered, closed, cancelled).
    [ProtoContract]
    public class MatchReadyClosedMsg
    {
        [ProtoMember(1)] public long EventId;
    }
}
