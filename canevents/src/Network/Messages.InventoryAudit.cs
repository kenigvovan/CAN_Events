using ProtoBuf;

namespace canevents.src.Network
{
    // Sent for /eventadmin inv gui.
    [ProtoContract]
    public class OpenInventoryAuditMsg { }

    // Expensive (reads the save for offline players), so only on request.
    [ProtoContract]
    public class RequestInventoryAuditMsg
    {
        // Substring match, filtered server-side. null = everyone.
        [ProtoMember(1)] public string NameFilter;

        // Otherwise only players holding a snapshot.
        [ProtoMember(2)] public bool IncludeEveryone;

        // 0 = no history.
        [ProtoMember(3)] public int HistoryLimit;

        // null = everyone's history.
        [ProtoMember(4)] public string HistoryUid;
    }

    [ProtoContract]
    public class InventoryAuditRowMsg
    {
        [ProtoMember(1)] public string PlayerUid;
        [ProtoMember(2)] public string PlayerName;
        [ProtoMember(3)] public bool Online;
        // In a match, so holding a snapshot is expected.
        [ProtoMember(4)] public bool InMatch;

        [ProtoMember(5)] public bool HasActive;
        [ProtoMember(6)] public int ActiveStacks;
        [ProtoMember(7)] public string ActiveEventCode;
        // -1 = old snapshot without a timestamp.
        [ProtoMember(8)] public long ActiveAgeSeconds;
        // Own-gear match: recorded only, never restored.
        [ProtoMember(9)] public bool ActiveKeepLive;

        [ProtoMember(10)] public bool HasParked;
        [ProtoMember(11)] public int ParkedStacks;
        [ProtoMember(12)] public string ParkedEventCode;
        [ProtoMember(13)] public long ParkedAgeSeconds;

        // Couldn't read the data at all, not the same as "nothing held".
        [ProtoMember(14)] public bool Unreadable;

        // History has a "taken" with no "returned" after it.
        [ProtoMember(15)] public bool UnsettledInHistory;
    }

    [ProtoContract]
    public class InventoryAuditHistoryMsg
    {
        [ProtoMember(1)] public long AtUnix;
        // InventoryAuditKind as int, so unknown values from a newer server don't throw.
        [ProtoMember(2)] public int Kind;
        [ProtoMember(3)] public string PlayerUid;
        [ProtoMember(4)] public string PlayerName;
        [ProtoMember(5)] public string EventCode;
        [ProtoMember(6)] public int StackCount;
        [ProtoMember(7)] public string Detail;
    }

    [ProtoContract]
    public class InventoryAuditListMsg
    {
        [ProtoMember(1)] public InventoryAuditRowMsg[] Rows;
        [ProtoMember(2)] public InventoryAuditHistoryMsg[] History;
        // Off in config = empty history, list still works.
        [ProtoMember(3)] public bool AuditEnabled;
        [ProtoMember(4)] public int PlayersChecked;
        [ProtoMember(5)] public int Unreadable;
    }

    [ProtoContract]
    public class RequestInventoryBackupDetailMsg
    {
        [ProtoMember(1)] public string PlayerUid;
        [ProtoMember(2)] public bool Parked;
    }

    [ProtoContract]
    public class InventoryBackupDetailMsg
    {
        [ProtoMember(1)] public string PlayerUid;
        [ProtoMember(2)] public string PlayerName;
        [ProtoMember(3)] public bool Parked;
        [ProtoMember(4)] public bool Present;
        [ProtoMember(5)] public long SavedAtUnix;
        [ProtoMember(6)] public string EventCode;
        [ProtoMember(7)] public int StackCount;
        [ProtoMember(8)] public bool KeepLive;
        [ProtoMember(9)] public float Health;
        [ProtoMember(10)] public float MaxHealth;

        [ProtoMember(11)] public string[] ItemCodes;
        [ProtoMember(12)] public int[] Quantities;
        // False if the code no longer resolves (removed mod).
        [ProtoMember(13)] public bool[] Resolved;
    }

    public enum InventoryBackupAction
    {
        RestoreParked = 0
    }

    [ProtoContract]
    public class InventoryBackupActionMsg
    {
        [ProtoMember(1)] public InventoryBackupAction Action;
        [ProtoMember(2)] public string PlayerUid;
    }
}
