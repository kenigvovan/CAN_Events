using ProtoBuf;

namespace canevents.src.Network
{
    // The signup window: an admin opens registration for an event and everyone online is invited by
    // a popup instead of having to hear about it and type /canreg in time.

    /// <summary>Server -> player: registration for this event is open. The seconds are what the
    /// client counts down on screen; the deadline itself is the server's.</summary>
    [ProtoContract]
    public class EventInviteMsg
    {
        [ProtoMember(1)] public string EventCode;
        [ProtoMember(2)] public string Category;
        /// <summary>Time left in the window, or -1 for a window with no deadline (admin starts it).</summary>
        [ProtoMember(3)] public float SecondsLeft;
        /// <summary>Sides queued so far, and how many the mode takes. MaxParties 0 = unknown.</summary>
        [ProtoMember(4)] public int Registered;
        [ProtoMember(5)] public int MaxParties;
        [ProtoMember(6)] public bool YouAreIn;
        /// <summary>A refresh of an already-sent invite: it updates an open dialog but must never
        /// re-open one the player dismissed.</summary>
        [ProtoMember(7)] public bool IsUpdate;
    }

    /// <summary>Server -> player: the window is over (started, filled up, cancelled).</summary>
    [ProtoContract]
    public class EventInviteClosedMsg
    {
        [ProtoMember(1)] public string EventCode;
        [ProtoMember(2)] public string Reason;   // started|full|cancelled|empty
    }
}
