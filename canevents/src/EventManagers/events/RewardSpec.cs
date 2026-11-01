using System.Collections.Generic;
using Newtonsoft.Json;

namespace canevents.src.EventManagers.events
{
    public class RewardItem
    {
        [JsonProperty] public string Code { get; set; }
        [JsonProperty] public int Quantity { get; set; } = 1;
    }

    // One placement bracket; every player at that rank gets the items (whole team in team modes).
    public class RewardTier
    {
        [JsonProperty] public List<RewardItem> Items { get; set; }
    }

    // "reward" object in the event JSON. Null fields fall back to Config.
    public class RewardSpec
    {
        // Used when PlacementRewards is unset or the event can't be ranked.
        [JsonProperty] public List<RewardItem> WinnerItems { get; set; }

        // [0] = 1st. Replaces WinnerItems when the event produces a ranking.
        [JsonProperty] public List<RewardTier> PlacementRewards { get; set; }

        // Shorter matches pay no winner reward. Null = Config.minMatchDurationSecondsForReward, 0 = off.
        [JsonProperty] public int? MinMatchSeconds { get; set; }

        [JsonProperty] public bool? RateCapExempt { get; set; }

        // Own reward-cap bucket. Null = Config.rewardRateCapPerEvent.
        [JsonProperty] public bool? SeparateRewardCap { get; set; }

        // Null = Config.rewardParticipation.
        [JsonProperty] public bool? Participation { get; set; }

        [JsonProperty] public List<RewardItem> ParticipationItems { get; set; }

        // Bots on the roster: "none" (default) pays nothing, "scaled" pays the human share of the lobby,
        // "full" pays as usual. Meant for co-op modes where bots fill a party; PvP bot fill stays "none".
        [JsonProperty] public string AllowWithBots { get; set; }
    }

    public enum BotRewardMode { None, Scaled, Full }

    public static class BotRewardModes
    {
        public static BotRewardMode Of(RewardSpec spec)
        {
            switch (spec?.AllowWithBots?.Trim().ToLowerInvariant())
            {
                case "scaled": return BotRewardMode.Scaled;
                case "full": return BotRewardMode.Full;
                default: return BotRewardMode.None;
            }
        }
    }
}
