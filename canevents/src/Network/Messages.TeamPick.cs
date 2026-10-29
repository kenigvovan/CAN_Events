using ProtoBuf;

namespace canevents.src.Network
{
    // Side pick in a waiting room (WaitingRoomEB). The server resends the whole state on every
    // change, so the client never has to merge anything.

    // Server -> player in the waiting room.
    [ProtoContract]
    public class TeamPickStateMsg
    {
        [ProtoMember(1)] public long EventId;
        [ProtoMember(2)] public string EventCode;
        [ProtoMember(3)] public float SecondsLeft;
        [ProtoMember(4)] public int MaxImbalance;

        // Per team, same index as the team: player names joined with '\n'.
        [ProtoMember(5)] public string[] Members;

        // -1 = not picked yet.
        [ProtoMember(6)] public int YourTeam = -1;

        // Match started or gone: take the dialog down.
        [ProtoMember(7)] public bool Closed;
    }

    // Player -> server.
    [ProtoContract]
    public class TeamPickResponseMsg
    {
        [ProtoMember(1)] public long EventId;
        [ProtoMember(2)] public int Team;
    }
}
