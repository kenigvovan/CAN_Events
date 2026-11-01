using canevents.src.EventManagers;
using ProtoBuf;

namespace canevents.src.Network
{
    // Always id 0, fields only appended, so any two mod versions can read it. See CanEventsProtocol.
    [ProtoContract]
    public class ProtocolHandshakeMsg
    {
        [ProtoMember(1)] public int ProtocolVersion;

        // Only for the error message, ProtocolVersion decides.
        [ProtoMember(2)] public string ModVersion;
    }

    [ProtoContract]
    public class EventStateUpdateMsg
    {
        // Was a plain int; protobuf writes enums as int, so still wire-compatible.
        [ProtoMember(1)]
        public EventState EventState;

        [ProtoMember(2)]
        public string GameModeCode;     // e.g. "1x1", "2x2", "ffa"

        [ProtoMember(3)]
        public string ArenaName;

        [ProtoMember(4)]
        public float CountdownSeconds;

        [ProtoMember(5)]
        public float TimeRemainingSeconds;

        [ProtoMember(6)]
        public float MaxDurationSeconds;

        [ProtoMember(7)]
        public long EventId;

        [ProtoMember(8)]
        public string StartCryLangKey;   // Lang key for the big text shown when countdown hits 0

        // In-match hold like the HG pedestal freeze; start cry waits for it. 0 = no hold.
        [ProtoMember(9)]
        public float HoldSeconds;

        [ProtoMember(10)]
        public string HoldLangKey;
    }

    [ProtoContract]
    public class ScoreboardUpdateMsg
    {
        [ProtoMember(1)]
        public ScoreEntryMsg[] Entries;

        [ProtoMember(2)]
        public long EventId;

        // null = per-player rows only.
        [ProtoMember(3)]
        public int[] TeamScores;

        // -1 = no zone / neutral, -2 = contested, >= 0 = owning team.
        [ProtoMember(4)]
        public int ZoneOwnerTeam;

        // 0 = no limit.
        [ProtoMember(5)]
        public int TeamScoreLimit;

        // Boss bar; empty name = no boss right now. Older clients skip unknown fields.
        [ProtoMember(6)]
        public string BossName;

        // 0..1
        [ProtoMember(7)]
        public float BossHealth;
    }

    // Nested only, not registered on the channel (same for the other *EntryMsg types).
    [ProtoContract]
    public class ScoreEntryMsg
    {
        [ProtoMember(1)]
        public string PlayerName;

        [ProtoMember(2)]
        public int TeamId;

        [ProtoMember(3)]
        public int Kills;

        [ProtoMember(4)]
        public int Deaths;

        [ProtoMember(5)]
        public bool Alive;
    }

    [ProtoContract]
    public class KillFeedMsg
    {
        [ProtoMember(1)]
        public string KillerName;

        [ProtoMember(2)]
        public string VictimName;

        [ProtoMember(3)]
        public string Method;   // "killed", "eliminated", "fell"

        [ProtoMember(4)]
        public long EventId;
    }

    [ProtoContract]
    public class EventResultMsg
    {
        [ProtoMember(1)]
        public string[] WinnerNames;

        [ProtoMember(2)]
        public ScoreEntryMsg[] FinalScores;

        [ProtoMember(3)]
        public float DurationSeconds;

        [ProtoMember(4)]
        public long EventId;
    }

    [ProtoContract]
    public class EventListMsg
    {
        [ProtoMember(1)]
        public EventListEntryMsg[] Events;
    }

    [ProtoContract]
    public class EventListEntryMsg
    {
        [ProtoMember(1)]
        public string GameModeCode;

        [ProtoMember(2)]
        public string ArenaName;

        [ProtoMember(3)]
        public int CurrentPlayers;

        [ProtoMember(4)]
        public int MaxPlayers;

        [ProtoMember(5)]
        public EventState State;

        [ProtoMember(6)]
        public long EventId;

        [ProtoMember(7)]
        public bool IsQueued;

        [ProtoMember(8)]
        public LoadoutSetInfoMsg[] LoadoutSets;

        [ProtoMember(11)]
        public int MinPlayers;

        [ProtoMember(12)]
        public string Category;

        [ProtoMember(13)]
        public bool Rated;

        // Packed "code:qty;code:qty", null = free.
        [ProtoMember(14)]
        public string EntryFeeItems;
        // Share of the pot burned.
        [ProtoMember(15)]
        public int EntryFeeRakePercent;

        // Either WinnerItems or PlacementRewards is set, not both. Packed "code:qty;...".
        [ProtoMember(16)]
        public string WinnerItems;
        [ProtoMember(17)]
        public RewardTierMsg[] PlacementRewards;
        [ProtoMember(18)]
        public string ParticipationItems;

        // Reward rate cap for this viewer and this event. RewardsRemaining <= 0 = capped.
        [ProtoMember(19)]
        public bool RewardCapActive;
        [ProtoMember(20)]
        public int RewardsRemaining;
        [ProtoMember(21)]
        public double SecondsToNextReward;

        // Viewer's own wait in this queue, 0 if not queued.
        [ProtoMember(22)]
        public double QueuedSeconds;

        // Lobbies only: the match runs a shop that may be looked at before joining.
        [ProtoMember(23)]
        public bool HasShop;
    }

    [ProtoContract]
    public class RewardTierMsg
    {
        [ProtoMember(1)] public int Place;     // 1-based finishing place
        [ProtoMember(2)] public string Items;  // packed "code:qty;code:qty"
    }

    // Sent once on join.
    [ProtoContract]
    public class ClientConfigSyncMsg
    {
        [ProtoMember(1)]
        public string MainDialogHotkey;

        // UI hint only, the server re-checks on request.
        [ProtoMember(2)]
        public bool MaySpectate;

        // Enables ClientTrace logging to client-main.log.
        [ProtoMember(3)]
        public bool Diagnostics;
    }

    [ProtoContract]
    public class JoinEventRequestMsg
    {
        [ProtoMember(1)]
        public string EventCode;    // which event to join, e.g. "1x1"

        [ProtoMember(2)]
        public string LoadoutSet;   // chosen loadout set name, null = default
    }

    [ProtoContract]
    public class LeaveEventRequestMsg
    {
        [ProtoMember(1)]
        public string EventCode; // null = leave all, specific code = leave that queue
    }

    [ProtoContract]
    public class RequestEventListMsg
    {
    }

    [ProtoContract]
    public class RequestTemplateListMsg
    {
    }

    [ProtoContract]
    public class TemplateListMsg
    {
        [ProtoMember(1)]
        public TemplateListEntryMsg[] Templates;
    }

    [ProtoContract]
    public class TemplateListEntryMsg
    {
        [ProtoMember(1)]
        public string Code;

        [ProtoMember(2)]
        public string Description;

        [ProtoMember(3)]
        public string PlaceTypes;

        [ProtoMember(4)]
        public bool OnDemand;
    }

    // Placed at the player's position.
    [ProtoContract]
    public class PlaceTemplateMsg
    {
        [ProtoMember(1)]
        public string TemplateCode;
    }

    [ProtoContract]
    public class RemovePlaceWithRestoreMsg
    {
        [ProtoMember(1)]
        public long PlaceId;
    }

    public enum TplAction
    {
        Start = 0,
        AddSpawn = 1,
        Clear = 2,
        Save = 3,
        Cancel = 4,
        Finish = 5,
        RequestState = 6
    }

    [ProtoContract]
    public class TplActionMsg
    {
        [ProtoMember(1)]
        public TplAction Action;

        [ProtoMember(2)]
        public string TemplateCode;  // for Start
    }

    [ProtoContract]
    public class TplSessionStateMsg
    {
        [ProtoMember(1)]
        public bool InSession;

        [ProtoMember(2)]
        public string TemplateCode;

        [ProtoMember(3)]
        public long PlaceId;

        [ProtoMember(4)]
        public int[] OffsetsX;

        [ProtoMember(5)]
        public int[] OffsetsY;

        [ProtoMember(6)]
        public int[] OffsetsZ;

        [ProtoMember(7)]
        public int OriginX;
        [ProtoMember(8)]
        public int OriginY;
        [ProtoMember(9)]
        public int OriginZ;
    }

    [ProtoContract]
    public class RequestSchematicListMsg
    {
    }

    [ProtoContract]
    public class SchematicListMsg
    {
        [ProtoMember(1)]
        public SchematicListEntryMsg[] Schematics;
    }

    [ProtoContract]
    public class SchematicListEntryMsg
    {
        [ProtoMember(1)]
        public string Name;     // filename without .json
        [ProtoMember(2)]
        public int SizeX;
        [ProtoMember(3)]
        public int SizeY;
        [ProtoMember(4)]
        public int SizeZ;
        [ProtoMember(5)]
        public bool HasTemplate; // a template already references this schematic
    }

    [ProtoContract]
    public class CreateTemplateFromSchematicMsg
    {
        [ProtoMember(1)]
        public string SchematicName;   // filename w/o extension
        [ProtoMember(2)]
        public string TemplateCode;    // desired code for new ArenaTemplate
        [ProtoMember(3)]
        public string[] PlaceTypes;
        [ProtoMember(4)]
        public bool OnDemand;          // if true, schematic appears only during events
    }

}
