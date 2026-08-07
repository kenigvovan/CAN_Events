using System.Collections.Generic;
using Newtonsoft.Json;

namespace canevents.src.EventManagers.events
{
    public class RewardItem
    {
        [JsonProperty] public string Code { get; set; }
        [JsonProperty] public int Quantity { get; set; } = 1;
    }

    // One placement bracket (1st place, 2nd place, ...). Items are granted to every player who
    // finishes at this rank: a single player for FFA, a whole team for team events.
    public class RewardTier
    {
        [JsonProperty] public List<RewardItem> Items { get; set; }
    }

    // Optional per-event reward override, configured under a "reward" object in the event JSON. A set
    // field replaces the single-item global default in Config; a null field falls back to it.
    public class RewardSpec
    {
        // Flat item set granted to every winner, used when PlacementRewards is unset or the event
        // can't be ranked. Null/empty falls back to the global single-item winner reward.
        [JsonProperty] public List<RewardItem> WinnerItems { get; set; }

        // Rewards by finishing rank: [0] = 1st, [1] = 2nd, ... Requires the event to produce a
        // ranking; when it does, this REPLACES the flat winner reward. Ignored for unrankable events.
        [JsonProperty] public List<RewardTier> PlacementRewards { get; set; }

        // Anti-throw contribution gate: matches shorter than this pay no winner/placement reward. Null
        // falls back to Config.minMatchDurationSecondsForReward; 0 disables the gate for this event.
        [JsonProperty] public int? MinMatchSeconds { get; set; }

        // Exempts the event from the reward rate-cap (e.g. admin-run tournaments where legit winners
        // must always be paid). Null/false = subject to the cap.
        [JsonProperty] public bool? RateCapExempt { get; set; }

        // Whether this event uses its OWN reward-cap bucket instead of the shared one. Null falls back
        // to Config.rewardRateCapPerEvent.
        [JsonProperty] public bool? SeparateRewardCap { get; set; }

        // Whether non-winners get a reward. Null falls back to Config.rewardParticipation.
        [JsonProperty] public bool? Participation { get; set; }

        // Item set granted to each participant. Null/empty falls back to the global single-item
        // participation reward.
        [JsonProperty] public List<RewardItem> ParticipationItems { get; set; }
    }
}
