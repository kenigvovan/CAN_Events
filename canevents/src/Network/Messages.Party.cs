using ProtoBuf;

namespace canevents.src.Network
{
    // Party messages

    // ProtoBuf sends the underlying int: never renumber or reorder, add new actions at the end.
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

    [ProtoContract]
    public class PartyActionMsg
    {
        [ProtoMember(1)] public PartyAction Action;
        [ProtoMember(2)] public string TargetName; // for invite/kick
        [ProtoMember(3)] public string InviterName; // for accept/decline
    }

    // Recipient's current party, empty if none.
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

    // Result toast.
    [ProtoContract]
    public class PartyOperationResultMsg
    {
        [ProtoMember(1)] public bool Success;
        [ProtoMember(2)] public string Message;
    }
}
