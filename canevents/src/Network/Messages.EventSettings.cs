using ProtoBuf;

namespace canevents.src.Network
{
    // Admin editor for an event's behavior settings, see CANEventManager.Settings.

    [ProtoContract]
    public class RequestEventSettingsMsg
    {
        [ProtoMember(1)] public string EventCode;
    }

    // Every scalar setting the event's behaviors read, in behavior order.
    [ProtoContract]
    public class EventSettingsMsg
    {
        [ProtoMember(1)] public string EventCode;
        [ProtoMember(2)] public EventSettingRowMsg[] Rows;
    }

    // Values as invariant-culture text, like ServerSettingRowMsg.
    [ProtoContract]
    public class EventSettingRowMsg
    {
        [ProtoMember(1)] public string Behavior;
        [ProtoMember(2)] public string Key;
        [ProtoMember(3)] public int Type;      // EventManagers.events.SettingType
        [ProtoMember(4)] public string Value;
        [ProtoMember(5)] public string Default;
    }

    // Answered with a result toast and a fresh EventSettingsMsg.
    [ProtoContract]
    public class SetEventSettingMsg
    {
        [ProtoMember(1)] public string EventCode;
        [ProtoMember(2)] public string Behavior;
        [ProtoMember(3)] public string Key;
        [ProtoMember(4)] public string Value;
    }
}
