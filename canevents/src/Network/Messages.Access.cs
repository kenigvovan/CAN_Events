using ProtoBuf;

namespace canevents.src.Network
{
    // Per-player access control, as an admin panel surface: who may watch matches, and who is barred
    // from the queue. Both were command-only before - the panel had no player management at all -
    // which meant an admin had to leave the panel and remember two command spellings to do either.

    /// <summary>Client asks for the access list.</summary>
    [ProtoContract]
    public class RequestPlayerAccessMsg { }

    /// <summary>
    /// Who the server knows about and what they may do. Deliberately not every player who ever
    /// joined: online players plus anyone already granted or barred. A long-lived server's full
    /// roster is thousands of names, and the panel is for acting on a few of them.
    /// </summary>
    [ProtoContract]
    public class PlayerAccessListMsg
    {
        [ProtoMember(1)] public PlayerAccessEntryMsg[] Players;

        /// <summary>Names known to the server but left out of the list above, so the panel can say so
        /// rather than look like it lost them.</summary>
        [ProtoMember(2)] public int OmittedCount;
    }

    [ProtoContract]
    public class PlayerAccessEntryMsg
    {
        [ProtoMember(1)] public string PlayerUid;
        [ProtoMember(2)] public string PlayerName;
        [ProtoMember(3)] public bool Online;

        /// <summary>May watch live matches.</summary>
        [ProtoMember(4)] public bool MaySpectate;

        /// <summary>The spectate right comes from their role, not from a personal grant. Revoking it
        /// per player is still possible (it becomes a denial), but the panel has to say where the
        /// right came from or "revoke" looks like it did nothing to the role.</summary>
        [ProtoMember(5)] public bool SpectateFromRole;

        /// <summary>Barred from every event queue.</summary>
        [ProtoMember(6)] public bool QueueBlacklisted;

        [ProtoMember(7)] public string RoleCode;
    }

    /// <summary>What the panel can change about one player.</summary>
    public enum PlayerAccessAction
    {
        GrantSpectate = 0,
        RevokeSpectate = 1,
        BlacklistAdd = 2,
        BlacklistRemove = 3
    }

    /// <summary>Client asks to change one player's access. Admin-gated on arrival.</summary>
    [ProtoContract]
    public class PlayerAccessActionMsg
    {
        [ProtoMember(1)] public PlayerAccessAction Action;

        /// <summary>Who to act on. A uid when the panel has one; otherwise a name typed into the box,
        /// which the server resolves - that is how an admin grants the right to somebody who has
        /// never been on the list.</summary>
        [ProtoMember(2)] public string PlayerUid;
        [ProtoMember(3)] public string PlayerName;
    }
}
