using ProtoBuf;

namespace canevents.src.Network
{
    [ProtoContract]
    public class RequestAdminEventListMsg { }

    [ProtoContract]
    public class AdminEventListMsg
    {
        [ProtoMember(1)] public AdminEventListEntryMsg[] Events;
    }

    // Uses the same check as DefaultEventPlacesManager.TryFindEventPlace.
    public enum EventReadiness
    {
        Ready = 0,
        NoPlaceWithTags = 1,
        AllBusy = 2,
        AllDisabled = 3,
        NoSpawns = 4,
        EventDisabled = 5,
        // Arenas lack mode data, see PlaceSetupCheck.
        IncompleteSetup = 6
    }

    [ProtoContract]
    public class AdminEventListEntryMsg
    {
        [ProtoMember(1)] public string Code;
        [ProtoMember(2)] public string Category;
        [ProtoMember(3)] public bool Enabled;

        // A place must carry all of these tags.
        [ProtoMember(4)] public string[] EventPlaceTypes;
        [ProtoMember(5)] public int MinPlayers;
        [ProtoMember(6)] public int MaxPlayers;
        [ProtoMember(7)] public bool Rated;
        // Was a plain int; protobuf writes enums as int, so still wire-compatible.
        [ProtoMember(8)] public EventReadiness Readiness;
        [ProtoMember(9)] public string ReadinessDetail; // place name, or what is missing
        // One per team.
        [ProtoMember(10)] public int RequiredSpawns;
        // Has a WaitingRoom behavior: the arena wizard asks for /canep waitroom.
        [ProtoMember(11)] public bool NeedsWaitingRoom;
        // Open match (no queue, walked into through pads).
        [ProtoMember(12)] public bool IsOpenMatch;
        // Teams a match is played with (CANEventManager.TeamCountOf).
        [ProtoMember(13)] public int Teams;
    }

    // Persisted in ModConfig.
    [ProtoContract]
    public class ToggleEventEnabledMsg
    {
        [ProtoMember(1)] public string Code;
    }

    public enum AdminAction
    {
        ForceStart = 0,
        StopAll = 1,
        ScheduleOn = 2,
        ScheduleOff = 3,
        ScheduleInterval = 4,
        // Test match against bots, see AdminActionMsg.BotMode.
        BotMatch = 5,
        // IntValue minutes, 0 = until the admin starts it.
        OpenSignup = 6,
        CancelSignup = 7,
        RotationAdd = 8,
        RotationRemove = 9
    }

    // Play = 0 is the old behaviour, so older clients that don't send it still work.
    public enum BotMatchMode
    {
        // Caller fights the bots.
        Play = 0,

        // Bots only, caller is seated as an observer.
        Watch = 1,

        // Bots only, caller isn't involved and can join as a spectator later.
        Unattended = 2
    }

    // Packets instead of chat commands so the panel gets a PlaceOperationResultMsg back.
    [ProtoContract]
    public class AdminActionMsg
    {
        [ProtoMember(1)] public AdminAction Action;
        [ProtoMember(2)] public string EventCode;  // ForceStart, OpenSignup, CancelSignup, BotMatch
        [ProtoMember(3)] public int IntValue;      // ScheduleInterval: minutes

        [ProtoMember(4)] public BotMatchMode BotMode;
    }

    [ProtoContract]
    public class RequestScheduleStateMsg { }

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
        [ProtoMember(10)] public string[] OpenSignupCodes;
        // Parallel to OpenSignupCodes; -1 = no deadline.
        [ProtoMember(11)] public float[] OpenSignupSeconds;
    }
}
