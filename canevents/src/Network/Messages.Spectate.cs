using ProtoBuf;

namespace canevents.src.Network
{
    // Privileged observers only. Never send to eliminated players, it would be a wallhack.
    [ProtoContract]
    public class SpectatorTelemetryMsg
    {
        [ProtoMember(1)] public long EventId;

        [ProtoMember(2)] public string[] Uids;
        [ProtoMember(3)] public string[] Names;
        [ProtoMember(4)] public int[] TeamIds;

        [ProtoMember(5)] public double[] X;
        [ProtoMember(6)] public double[] Y;
        [ProtoMember(7)] public double[] Z;

        [ProtoMember(8)] public float[] Health;
        [ProtoMember(9)] public float[] MaxHealth;

        // Eliminated bodies can linger in respawn modes.
        [ProtoMember(10)] public bool[] Alive;

        [ProtoMember(11)] public bool[] IsBot;
    }

    [ProtoContract]
    public class SpectateRequestMsg
    {
        // 0 with Start = the only running match.
        [ProtoMember(1)] public long EventId;

        // false = leave the seat, same as /canspectate off.
        [ProtoMember(2)] public bool Start;
    }

    // No "jump to player" here: the broadcaster panel is a HUD and can't take clicks.
    // Use /canspectate follow for that.
    [ProtoContract]
    public class SpectateFollowMsg
    {
        [ProtoMember(1)] public long EventId;

        // +1 / -1
        [ProtoMember(2)] public int Step;
    }

    // Teleports don't carry rotation, so the client sets the camera via IInputAPI.MouseYaw.
    [ProtoContract]
    public class SpectateCameraMsg
    {
        [ProtoMember(1)] public float Yaw;
        [ProtoMember(2)] public float Pitch;
    }

    [ProtoContract]
    public class SpectateStateMsg
    {
        [ProtoMember(1)] public bool Observing;
        [ProtoMember(2)] public long EventId;
        [ProtoMember(3)] public string Message;

        // UI hint, mirrors the server's privilege check.
        [ProtoMember(4)] public bool Privileged;
    }
}
