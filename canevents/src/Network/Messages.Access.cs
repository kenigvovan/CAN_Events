using ProtoBuf;

namespace canevents.src.Network
{
    [ProtoContract]
    public class RequestPlayerAccessMsg { }

    // Online players plus anyone with a grant or ban, not the whole roster.
    [ProtoContract]
    public class PlayerAccessListMsg
    {
        [ProtoMember(1)] public PlayerAccessEntryMsg[] Players;

        // Entries cut off by the list cap.
        [ProtoMember(2)] public int OmittedCount;
    }

    [ProtoContract]
    public class PlayerAccessEntryMsg
    {
        [ProtoMember(1)] public string PlayerUid;
        [ProtoMember(2)] public string PlayerName;
        [ProtoMember(3)] public bool Online;

        [ProtoMember(4)] public bool MaySpectate;

        // Right comes from the role; revoking then becomes a per-player denial.
        [ProtoMember(5)] public bool SpectateFromRole;

        [ProtoMember(6)] public bool QueueBlacklisted;

        [ProtoMember(7)] public string RoleCode;
    }

    public enum PlayerAccessAction
    {
        GrantSpectate = 0,
        RevokeSpectate = 1,
        BlacklistAdd = 2,
        BlacklistRemove = 3
    }

    [ProtoContract]
    public class PlayerAccessActionMsg
    {
        [ProtoMember(1)] public PlayerAccessAction Action;

        // Uid if known, otherwise PlayerName is resolved by the server.
        [ProtoMember(2)] public string PlayerUid;
        [ProtoMember(3)] public string PlayerName;
    }
}
