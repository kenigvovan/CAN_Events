using ProtoBuf;

namespace canevents.src.Network
{
    // Spectating a match you are not playing in. Two tiers, deliberately kept apart:
    //
    //   * the HUD feed (state, scoreboard, kill feed, zone) is what any spectator sees, including a
    //     player eliminated from the match they were in - it says no more than the players on the
    //     field already know;
    //   * the telemetry below is a broadcaster's view - live positions and health of everyone still
    //     fighting - and is only ever sent to an observer holding the admin privilege. Handing it to
    //     an eliminated player would turn dying into a wallhack for the rest of the match.

    /// <summary>
    /// Live participant readout for an observer, pushed a few times a second while at least one
    /// privileged observer is watching. Parallel arrays rather than a struct per player: this is the
    /// one packet on a sub-second timer, and the flat form keeps it small.
    /// </summary>
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

        /// <summary>Still in play. An eliminated body may linger (respawn modes), so "on the list"
        /// does not mean "fighting".</summary>
        [ProtoMember(10)] public bool[] Alive;

        /// <summary>A bot rather than a player. The broadcaster view labels them, since a bot name
        /// and a player name look alike on the roster.</summary>
        [ProtoMember(11)] public bool[] IsBot;
    }

    /// <summary>Client asks to start or stop watching a match. EventId 0 with Start means
    /// "the only match running".</summary>
    [ProtoContract]
    public class SpectateRequestMsg
    {
        [ProtoMember(1)] public long EventId;

        /// <summary>false = leave the spectator seat.</summary>
        [ProtoMember(2)] public bool Start;
    }

    /// <summary>
    /// An observer asks to be moved one fighter along - the camera work behind broadcasting a match.
    /// Privileged: the server re-checks that the caller is an observer of that very match and may
    /// spectate at all, since a client can send this at any time.
    ///
    /// There is no "jump to this player" here on purpose. Naming a target needs somewhere to click,
    /// and the broadcaster panel is a HUD element - not focusable, so the cursor is never released
    /// over it and a button on it can be drawn but never pressed. Picking a specific fighter is
    /// /canspectate follow instead.
    /// </summary>
    [ProtoContract]
    public class SpectateFollowMsg
    {
        [ProtoMember(1)] public long EventId;

        /// <summary>+1 / -1 to move to the next or previous participant.</summary>
        [ProtoMember(2)] public int Step;
    }

    /// <summary>
    /// Points the observer's camera somewhere, after the server moved them there. Camera angle is
    /// client-owned - a teleport carries coordinates only (see EntityPlayer.Onplrteleported, which
    /// broadcasts a Vec3d and nothing else), so the server cannot turn a player's head by writing to
    /// their entity. This asks the client to do it, which it can via IInputAPI.MouseYaw.
    /// </summary>
    [ProtoContract]
    public class SpectateCameraMsg
    {
        [ProtoMember(1)] public float Yaw;
        [ProtoMember(2)] public float Pitch;
    }

    /// <summary>Server's answer to a spectate request: what to tell the player, and whether their
    /// client should now consider itself an observer.</summary>
    [ProtoContract]
    public class SpectateStateMsg
    {
        [ProtoMember(1)] public bool Observing;
        [ProtoMember(2)] public long EventId;
        [ProtoMember(3)] public string Message;

        /// <summary>The caller may use the broadcaster tools (telemetry, follow). Mirrors the
        /// server-side privilege check so the client can hide what it may not use.</summary>
        [ProtoMember(4)] public bool Privileged;
    }
}
