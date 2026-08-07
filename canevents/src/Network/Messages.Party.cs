using ProtoBuf;

namespace canevents.src.Network
{
    // Party messages

    /// <summary>Party actions a client can request, as one message with a discriminator rather than
    /// eight message types. ProtoBuf sends the underlying int, so never renumber or reorder these -
    /// add new actions at the end.</summary>
    public enum PartyAction
    {
        Create = 0,
        Invite = 1,
        Accept = 2,
        Decline = 3,
        Leave = 4,
        Kick = 5,
        Disband = 6,
        RequestState = 7
    }

    /// <summary>Client-initiated party action.</summary>
    [ProtoContract]
    public class PartyActionMsg
    {
        [ProtoMember(1)] public PartyAction Action;
        [ProtoMember(2)] public string TargetName; // for invite/kick
        [ProtoMember(3)] public string InviterName; // for accept/decline
    }

    /// <summary>Server pushes the recipient's current party state (or empty if none).</summary>
    [ProtoContract]
    public class PartyStateMsg
    {
        [ProtoMember(1)] public bool InParty;
        [ProtoMember(2)] public string PartyGuid;
        [ProtoMember(3)] public string LeaderUid;
        [ProtoMember(4)] public PartyMemberMsg[] Members;
        [ProtoMember(5)] public PartyInviteMsg[] PendingInvitesForMe;
        [ProtoMember(6)] public int MaxSize;
        [ProtoMember(7)] public string SelfUid;
    }

    [ProtoContract]
    public class PartyMemberMsg
    {
        [ProtoMember(1)] public string Uid;
        [ProtoMember(2)] public string Name;
        [ProtoMember(3)] public bool IsLeader;
        [ProtoMember(4)] public bool Online;
    }

    [ProtoContract]
    public class PartyInviteMsg
    {
        [ProtoMember(1)] public string PartyGuid;
        [ProtoMember(2)] public string InviterName;
        [ProtoMember(3)] public float SecondsRemaining;
    }

    /// <summary>Server tells client about a transient operation result (toast).</summary>
    [ProtoContract]
    public class PartyOperationResultMsg
    {
        [ProtoMember(1)] public bool Success;
        [ProtoMember(2)] public string Message;
    }
}
