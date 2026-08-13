using canevents.src.Duels;
using ProtoBuf;

namespace canevents.src.Network
{
    /// <summary>Client -> server: challenge a named player.</summary>
    [ProtoContract]
    public class DuelChallengeMsg
    {
        [ProtoMember(1)] public string TargetName;
        [ProtoMember(2)] public DuelKind Kind;

        /// <summary>Stake, packed "code:qty;code:qty" - the same shape the event list uses for fees
        /// and prizes. Ignored unless <see cref="Kind"/> is Wager.</summary>
        [ProtoMember(3)] public string WagerItems;

        /// <summary>The mode the challenger picked, or null for "whatever fits". Validated
        /// server-side against the duellable list.</summary>
        [ProtoMember(4)] public string EventCode;

        /// <summary>Match modifiers, wire form ("noarmor", "health:0.5"). Validated server-side
        /// against <see cref="EventManagers.events.EventMutators"/>.</summary>
        [ProtoMember(5)] public string[] Mutators;
    }

    /// <summary>Server -> challenged player: someone wants to fight, decide before the clock runs out.</summary>
    [ProtoContract]
    public class DuelPromptMsg
    {
        [ProtoMember(1)] public long ChallengeId;
        [ProtoMember(2)] public string ChallengerName;
        [ProtoMember(3)] public DuelKind Kind;

        /// <summary>Players per side, so a 3v3 does not arrive looking like a 1v1.</summary>
        [ProtoMember(4)] public int SideSize;

        [ProtoMember(5)] public float TimeoutSeconds;

        /// <summary>What each side puts up, packed; null for a friendly duel.</summary>
        [ProtoMember(6)] public string WagerItems;

        /// <summary>Event this will be played as, for the prompt to name it.</summary>
        [ProtoMember(7)] public string EventCode;

        /// <summary>Lifetime score between the two, from the challenged player's point of view.
        /// All zero when they have never met.</summary>
        [ProtoMember(8)] public int YourWinsVsChallenger;
        [ProtoMember(9)] public int ChallengerWinsVsYou;
        [ProtoMember(10)] public int DrawsVsChallenger;

        /// <summary>The challenger's match modifiers, wire form - the prompt names them so the target
        /// knows exactly what they are accepting.</summary>
        [ProtoMember(11)] public string[] Mutators;
    }

    /// <summary>Client -> server: answer the outstanding challenge.</summary>
    [ProtoContract]
    public class DuelResponseMsg
    {
        [ProtoMember(1)] public long ChallengeId;
        [ProtoMember(2)] public bool Accept;
    }

    /// <summary>Server -> client: drop the prompt (answered, expired, cancelled).</summary>
    [ProtoContract]
    public class DuelClosedMsg
    {
        [ProtoMember(1)] public long ChallengeId;
    }

    /// <summary>Client asks who can be challenged right now.</summary>
    [ProtoContract]
    public class RequestDuelTargetsMsg { }

    /// <summary>
    /// Who is online and free to fight. Deliberately separate from PlayerAccessListMsg: that one is
    /// admin-only and carries privileges, this one is for every player and carries nothing but names.
    /// </summary>
    [ProtoContract]
    public class DuelTargetListMsg
    {
        [ProtoMember(1)] public string[] PlayerNames;

        /// <summary>Side each name would bring - their party size, or 1. Lets the browser show
        /// "3v3" before the challenge is sent, and grey out mismatches.</summary>
        [ProtoMember(2)] public int[] SideSizes;

        /// <summary>The viewer's own side size, to compare the rows against.</summary>
        [ProtoMember(3)] public int YourSideSize;

        /// <summary>Modes the viewer's side could play, for the mode picker. Loadout set names ride
        /// along so the "loadout:X" term can offer a real list instead of a free-text field.</summary>
        [ProtoMember(4)] public DuelModeMsg[] Modes;

        /// <summary>Mutator keys the server's config forbids; the tab hides those knobs.</summary>
        [ProtoMember(5)] public string[] DisabledMutators;

        /// <summary>Lifetime score against each listed player, from the viewer's point of view -
        /// parallel to <see cref="PlayerNames"/>, like <see cref="SideSizes"/>. All zero for someone
        /// they have never fought.</summary>
        [ProtoMember(6)] public int[] WinsVs;
        [ProtoMember(7)] public int[] LossesVs;
        [ProtoMember(8)] public int[] DrawsVs;
    }

    /// <summary>One duellable mode for the picker in the duels tab.</summary>
    [ProtoContract]
    public class DuelModeMsg
    {
        [ProtoMember(1)] public string Code;
        [ProtoMember(2)] public string[] LoadoutSetNames;
    }
}
