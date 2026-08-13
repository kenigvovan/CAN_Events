using canevents.src.EventManagers;
using ProtoBuf;

namespace canevents.src.Network
{
    // Server → Client messages

    /// <summary>Sent on every event state transition (lobby open, countdown, start, finish).</summary>
    [ProtoContract]
    public class EventStateUpdateMsg
    {
        // ProtoBuf writes an enum as its underlying int, so this stays wire-compatible with the
        // plain int it used to be.
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
    }

    /// <summary>Sent periodically during Running state with the current scoreboard.</summary>
    [ProtoContract]
    public class ScoreboardUpdateMsg
    {
        [ProtoMember(1)]
        public ScoreEntryMsg[] Entries;

        [ProtoMember(2)]
        public long EventId;

        // Optional team-level totals (e.g. KOTH zone-hold seconds, DM team kills).
        // null = HUD shows only per-player rows.
        [ProtoMember(3)]
        public int[] TeamScores;

        // KOTH-style zone state. -1 = neutral / no zone, -2 = contested, >=0 = owning team.
        [ProtoMember(4)]
        public int ZoneOwnerTeam;

        // Target the TeamScores race to, so the HUD can draw progress. 0 = no limit.
        [ProtoMember(5)]
        public int TeamScoreLimit;
    }

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

    /// <summary>Sent on each kill/elimination.</summary>
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

    /// <summary>Sent when the event finishes, with final results.</summary>
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

    /// <summary>Response to the client's request for the event list.</summary>
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

        // Entry fee charged at match start, packed "code:qty;code:qty" (null = free). RakePercent is
        // the share of the pot burned; the winner keeps (100 - rake)%.
        [ProtoMember(14)]
        public string EntryFeeItems;
        [ProtoMember(15)]
        public int EntryFeeRakePercent;

        // Payout preview. Either WinnerItems (flat winner reward) or PlacementRewards (per-rank) is set,
        // never both. ParticipationItems is null when participation is off. All packed "code:qty;...".
        [ProtoMember(16)]
        public string WinnerItems;
        [ProtoMember(17)]
        public RewardTierMsg[] PlacementRewards;
        [ProtoMember(18)]
        public string ParticipationItems;

        // Per-viewer winner-reward rate-cap status FOR THIS EVENT: an event may be RateCapExempt, and in
        // per-event cap mode each event has its own bucket (in global mode all report the same numbers).
        // RewardCapActive = the cap governs this event for the viewer; RewardsRemaining <= 0 means capped.
        [ProtoMember(19)]
        public bool RewardCapActive;
        [ProtoMember(20)]
        public int RewardsRemaining;
        [ProtoMember(21)]
        public double SecondsToNextReward;

        /// <summary>How long the viewer has been in this queue. 0 when they are not in it. Their own
        /// wait, not the queue's oldest: it is the number that decides whether to keep waiting.</summary>
        [ProtoMember(22)]
        public double QueuedSeconds;
    }

    [ProtoContract]
    public class RewardTierMsg
    {
        [ProtoMember(1)] public int Place;     // 1-based finishing place
        [ProtoMember(2)] public string Items;  // packed "code:qty;code:qty"
    }

    /// <summary>
    /// Sent once when a player joins, carrying client-relevant config defaults the admin sets
    /// centrally for first-time players.
    /// </summary>
    [ProtoContract]
    public class ClientConfigSyncMsg
    {
        [ProtoMember(1)]
        public string MainDialogHotkey;

        /// <summary>May this player watch live matches? Sent on join so the browser can show the
        /// Watch button only to those it will work for. The server re-checks the request itself -
        /// this is a display hint, not the gate.</summary>
        [ProtoMember(2)]
        public bool MaySpectate;
    }

    // Client → Server messages

    /// <summary>Client requests to join an event.</summary>
    [ProtoContract]
    public class JoinEventRequestMsg
    {
        [ProtoMember(1)]
        public string EventCode;    // which event to join, e.g. "1x1"

        [ProtoMember(2)]
        public string LoadoutSet;   // chosen loadout set name, null = default
    }

    /// <summary>Client requests to leave the current event / queue.</summary>
    [ProtoContract]
    public class LeaveEventRequestMsg
    {
        [ProtoMember(1)]
        public string EventCode; // null = leave all, specific code = leave that queue
    }

    /// <summary>Client requests the list of available events.</summary>
    [ProtoContract]
    public class RequestEventListMsg
    {
    }

    // Arena template messages

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

    /// <summary>Client requests to place an arena template at their current position.</summary>
    [ProtoContract]
    public class PlaceTemplateMsg
    {
        [ProtoMember(1)]
        public string TemplateCode;
    }

    /// <summary>Client requests to remove a place and restore the original blocks.</summary>
    [ProtoContract]
    public class RemovePlaceWithRestoreMsg
    {
        [ProtoMember(1)]
        public long PlaceId;
    }

    // Template editor messages

    /// <summary>Template-editor actions a client can request. ProtoBuf serializes the underlying int.</summary>
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

    // Schematic management messages

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
