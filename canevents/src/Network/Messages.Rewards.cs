using ProtoBuf;

namespace canevents.src.Network
{
    // Admin reward / entry-fee editor messages

    [ProtoContract]
    public class RequestEventRewardConfigMsg
    {
        [ProtoMember(1)] public string EventCode;
    }

    // Sent both ways. Item sets packed "code:qty;code:qty" (ItemSetUtil); tri-state flags 0 inherit / 1 on / 2 off.
    [ProtoContract]
    public class EventRewardConfigMsg
    {
        [ProtoMember(1)] public string EventCode;
        [ProtoMember(2)] public string WinnerItems;          // packed; flat winner set
        [ProtoMember(3)] public RewardTierMsg[] Placement;   // per-rank tiers (Place + packed Items)
        [ProtoMember(4)] public int Participation;           // 0 inherit / 1 on / 2 off
        [ProtoMember(5)] public string ParticipationItems;   // packed
        [ProtoMember(6)] public string EntryFeeItems;        // packed; empty/null = no fee
        [ProtoMember(7)] public int EntryFeeRake;            // 0..100
        [ProtoMember(8)] public int MinMatchSeconds;         // -1 inherit, else seconds
        [ProtoMember(9)] public int RateCapExempt;           // 0 inherit / 1 on / 2 off
        [ProtoMember(10)] public int SeparateRewardCap;      // 0 inherit / 1 on / 2 off
    }

    [ProtoContract]
    public class RequestRewardLimitsMsg { }

    // Global reward / anti-farm limits, sent both ways. No inherit, hence plain types.
    [ProtoContract]
    public class RewardLimitsMsg
    {
        [ProtoMember(1)] public int RateCapBurst;             // 0 = cap disabled
        [ProtoMember(2)] public double RateCapRefillHours;    // hours to refill one reward slot
        [ProtoMember(3)] public bool RateCapPerEvent;         // separate bucket per (player, event)
        [ProtoMember(4)] public int MinMatchSeconds;          // contribution gate; 0 = off
        [ProtoMember(5)] public bool RefundForfeitOnVoid;     // entryFeeRefundForfeitOnVoid
        [ProtoMember(6)] public bool Participation;           // global participation reward on/off
        [ProtoMember(7)] public string WinnerItemCode;        // global default winner item
        [ProtoMember(8)] public int WinnerQuantity;
        [ProtoMember(9)] public string ParticipationItemCode; // global default participation item
        [ProtoMember(10)] public int ParticipationQuantity;
    }
}
