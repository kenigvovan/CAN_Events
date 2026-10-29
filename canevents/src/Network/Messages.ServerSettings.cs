using ProtoBuf;

namespace canevents.src.Network
{
    // Admin editor for the main server config (ModConfig/canevents.json), see ServerConfigEditor.

    [ProtoContract]
    public class RequestServerSettingsMsg { }

    // Every editable setting with its current value, in display order.
    [ProtoContract]
    public class ServerSettingsMsg
    {
        [ProtoMember(1)] public ServerSettingRowMsg[] Rows;
    }

    // Values travel as invariant-culture text whatever the kind, so one shape covers them all.
    [ProtoContract]
    public class ServerSettingRowMsg
    {
        [ProtoMember(1)] public string Key;          // the Config field name
        [ProtoMember(2)] public string GroupLangKey; // heading the row sits under
        [ProtoMember(3)] public string LabelLangKey;
        [ProtoMember(4)] public string DescLangKey;
        [ProtoMember(5)] public int Kind;            // ServerSettingKind
        [ProtoMember(6)] public string Value;        // "true"/"false", "12", "1.5", or the text
        [ProtoMember(7)] public double Min;          // numbers only
        [ProtoMember(8)] public double Max;
        [ProtoMember(9)] public string[] Options;    // Choice only
        [ProtoMember(10)] public bool RequiresRestart;
        // A restart-only setting whose saved value differs from what this server started with.
        [ProtoMember(11)] public bool PendingRestart;
    }

    // Answered with a result toast and a fresh ServerSettingsMsg.
    [ProtoContract]
    public class SetServerSettingMsg
    {
        [ProtoMember(1)] public string Key;
        [ProtoMember(2)] public string Value;        // invariant-culture text, as in ServerSettingRowMsg
    }
}
