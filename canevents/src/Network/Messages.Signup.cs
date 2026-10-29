using ProtoBuf;

namespace canevents.src.Network
{
    // The signup window: an admin opens registration for an event and everyone online is invited by
    // a popup instead of having to hear about it and type /canreg in time.

    // Server -> player: registration is open. The client only displays the countdown; the deadline is the server's.
    [ProtoContract]
    public class EventInviteMsg
    {
        [ProtoMember(1)] public string EventCode;
        [ProtoMember(2)] public string Category;
        // -1: no deadline, an admin starts it.
        [ProtoMember(3)] public float SecondsLeft;
        // Sides queued so far and how many the mode takes; MaxParties 0 = unknown.
        [ProtoMember(4)] public int Registered;
        [ProtoMember(5)] public int MaxParties;
        [ProtoMember(6)] public bool YouAreIn;
        // Refresh of a sent invite: updates an open dialog, never re-opens a dismissed one.
        [ProtoMember(7)] public bool IsUpdate;
    }

    // Server -> player: the window is over (started, filled up, cancelled).
    [ProtoContract]
    public class EventInviteClosedMsg
    {
        [ProtoMember(1)] public string EventCode;
        [ProtoMember(2)] public string Reason;   // started|full|cancelled|empty
    }
}
