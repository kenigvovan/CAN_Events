using canevents.src.Duels;
using ProtoBuf;

namespace canevents.src.Network
{
    [ProtoContract]
    public class DuelChallengeMsg
    {
        [ProtoMember(1)] public string TargetName;
        [ProtoMember(2)] public DuelKind Kind;

        // Packed "code:qty;code:qty". Only used for Wager.
        [ProtoMember(3)] public string WagerItems;

        // null = any fitting mode.
        [ProtoMember(4)] public string EventCode;

        // Wire form, e.g. "noarmor", "health:0.5". Server validates them.
        [ProtoMember(5)] public string[] Mutators;
    }

    [ProtoContract]
    public class DuelPromptMsg
    {
        [ProtoMember(1)] public long ChallengeId;
        [ProtoMember(2)] public string ChallengerName;
        [ProtoMember(3)] public DuelKind Kind;

        [ProtoMember(4)] public int SideSize;

        [ProtoMember(5)] public float TimeoutSeconds;

        // null for a friendly duel.
        [ProtoMember(6)] public string WagerItems;

        [ProtoMember(7)] public string EventCode;

        // Head-to-head record from the challenged player's side.
        [ProtoMember(8)] public int YourWinsVsChallenger;
        [ProtoMember(9)] public int ChallengerWinsVsYou;
        [ProtoMember(10)] public int DrawsVsChallenger;

        [ProtoMember(11)] public string[] Mutators;
    }

    [ProtoContract]
    public class DuelResponseMsg
    {
        [ProtoMember(1)] public long ChallengeId;
        [ProtoMember(2)] public bool Accept;
    }

    [ProtoContract]
    public class DuelClosedMsg
    {
        [ProtoMember(1)] public long ChallengeId;
    }

    [ProtoContract]
    public class RequestDuelTargetsMsg { }

    // Public list, names only. Not PlayerAccessListMsg, which is admin-only.
    [ProtoContract]
    public class DuelTargetListMsg
    {
        [ProtoMember(1)] public string[] PlayerNames;

        // Party size or 1, parallel to PlayerNames.
        [ProtoMember(2)] public int[] SideSizes;

        [ProtoMember(3)] public int YourSideSize;

        [ProtoMember(4)] public DuelModeMsg[] Modes;

        // Mutators disabled in server config.
        [ProtoMember(5)] public string[] DisabledMutators;

        // Viewer's record vs each player, parallel to PlayerNames.
        [ProtoMember(6)] public int[] WinsVs;
        [ProtoMember(7)] public int[] LossesVs;
        [ProtoMember(8)] public int[] DrawsVs;
    }

    [ProtoContract]
    public class DuelModeMsg
    {
        [ProtoMember(1)] public string Code;
        [ProtoMember(2)] public string[] LoadoutSetNames;
    }
}
