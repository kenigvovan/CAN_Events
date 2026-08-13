using ProtoBuf;

namespace canevents.src.Network
{
    // Admin event enable/disable messages

    /// <summary>Admin requests the full list of registered events incl. their current enabled state.</summary>
    [ProtoContract]
    public class RequestAdminEventListMsg { }

    [ProtoContract]
    public class AdminEventListMsg
    {
        [ProtoMember(1)] public AdminEventListEntryMsg[] Events;
    }

    /// <summary>Why an event can or cannot start right now. Computed against the same predicate the
    /// real lookup uses (DefaultEventPlacesManager.TryFindEventPlace), so the panel never lies.</summary>
    public enum EventReadiness
    {
        Ready = 0,
        NoPlaceWithTags = 1,
        AllBusy = 2,
        AllDisabled = 3,
        NoSpawns = 4,
        EventDisabled = 5
    }

    [ProtoContract]
    public class AdminEventListEntryMsg
    {
        [ProtoMember(1)] public string Code;
        [ProtoMember(2)] public string Category;
        [ProtoMember(3)] public bool Enabled;

        // Place tags this event requires; a place must carry all of them.
        [ProtoMember(4)] public string[] EventPlaceTypes;
        [ProtoMember(5)] public int MinPlayers;
        [ProtoMember(6)] public int MaxPlayers;
        [ProtoMember(7)] public bool Rated;
        // ProtoBuf serializes an enum as its underlying int, so this stays wire-compatible with
        // the plain int it used to be.
        [ProtoMember(8)] public EventReadiness Readiness;
        [ProtoMember(9)] public string ReadinessDetail; // place name, or what is missing
        // Spawn points needed for a full match, one per team.
        [ProtoMember(10)] public int RequiredSpawns;
    }

    /// <summary>Admin toggles an event's enabled flag (persisted in ModConfig).</summary>
    [ProtoContract]
    public class ToggleEventEnabledMsg
    {
        [ProtoMember(1)] public string Code;
    }

    // Admin control + scheduler state

    public enum AdminAction
    {
        AnnounceEvent = 0,
        StopAll = 1,
        ScheduleOn = 2,
        ScheduleOff = 3,
        ScheduleInterval = 4,
        /// <summary>Testing: start an event right now against bots. What the caller's own part in it
        /// is comes from <see cref="AdminActionMsg.BotMode"/>.</summary>
        BotMatch = 5,
        /// <summary>Open a registration window for IntValue minutes (0 = until the admin starts it).</summary>
        OpenSignup = 6,
        /// <summary>Close an open registration window without starting anything.</summary>
        CancelSignup = 7
    }

    /// <summary>
    /// What the admin who started a bot match does in it. Play is the default and matches the
    /// original behaviour, so an older client sending nothing here still gets what it expected.
    /// </summary>
    public enum BotMatchMode
    {
        /// <summary>In the line-up, fighting the bots. The usual reason to press the button.</summary>
        Play = 0,

        /// <summary>Bots only, and the caller is seated as an observer once it has an arena. The
        /// shortcut for looking at the spectator features immediately.</summary>
        Watch = 1,

        /// <summary>
        /// Bots only, and the caller is left entirely alone - not in the line-up, not seated.
        /// This is what you want to test the ordinary way in: the match is simply running, and you
        /// join it afterwards from the browser or /canspectate like anyone else would.
        /// </summary>
        Unattended = 2
    }

    /// <summary>Admin panel actions. Sent as packets rather than chat commands so they answer with
    /// PlaceOperationResultMsg and the panel can show success or failure itself.</summary>
    [ProtoContract]
    public class AdminActionMsg
    {
        [ProtoMember(1)] public AdminAction Action;
        [ProtoMember(2)] public string EventCode;  // AnnounceEvent
        [ProtoMember(3)] public int IntValue;      // ScheduleInterval: minutes

        /// <summary>BotMatch: what the caller's own part in it is. See <see cref="BotMatchMode"/>.</summary>
        [ProtoMember(4)] public BotMatchMode BotMode;
    }

    [ProtoContract]
    public class RequestScheduleStateMsg { }

    /// <summary>Live scheduler state for the dashboard.</summary>
    [ProtoContract]
    public class ScheduleStateMsg
    {
        [ProtoMember(1)] public bool Enabled;
        [ProtoMember(2)] public int IntervalMinutes;
        [ProtoMember(3)] public float SecondsUntilNext;  // -1 when disabled
        [ProtoMember(4)] public string NextEventCode;    // next rotation entry, null if none
        [ProtoMember(5)] public string[] RotationCodes;
        [ProtoMember(6)] public int MinPlayersToStart;
        [ProtoMember(7)] public int MaxConcurrentEvents;
        [ProtoMember(8)] public int OnlinePlayers;
        [ProtoMember(9)] public int RunningEvents;
        /// <summary>Events whose registration window is open right now.</summary>
        [ProtoMember(10)] public string[] OpenSignupCodes;
        /// <summary>Seconds left on each of them, parallel to the codes; -1 = no deadline.</summary>
        [ProtoMember(11)] public float[] OpenSignupSeconds;
    }
}
