using System.Collections.Generic;
using canevents.src.EventManagers.events;

namespace canevents.src.Scoring
{
    /// <summary>
    /// Works out *what* an event pays, without granting anything: per-event set if configured, else the
    /// global single-item default from Config, else nothing. Shared by the distributor and the event
    /// browser so the preview cannot drift from the actual payout.
    /// </summary>
    public static class RewardResolver
    {
        public static bool ParticipationEnabled(CANEvent ev, Config config)
            => ev.Reward?.Participation ?? config.rewardParticipation;

        /// <summary>
        /// The anti-throw contribution gate: how long a match must last before its outcome counts.
        /// Per-event override, else the global default; 0 disables it. Payout and standings must read
        /// the same value, or a raised per-event threshold would withhold items while still letting
        /// two accounts farm ranked standings with 5-second matches.
        /// </summary>
        public static int MinMatchSeconds(CANEvent ev, Config config)
            => ev.Reward?.MinMatchSeconds ?? config.minMatchDurationSecondsForReward;

        /// <summary>Whether this match is too short to count. See <see cref="MinMatchSeconds"/>.</summary>
        public static bool BelowContributionGate(CANEvent ev, Config config, float elapsedSeconds, out int minSeconds)
        {
            minSeconds = MinMatchSeconds(ev, config);
            return minSeconds > 0 && elapsedSeconds < minSeconds;
        }

        public static List<RewardItem> WinnerItems(CANEvent ev, Config config)
            => ResolveSet(ev.Reward?.WinnerItems, config.rewardWinnerItemCode, config.rewardWinnerQuantity);

        public static List<RewardItem> ParticipationItems(CANEvent ev, Config config)
            => ResolveSet(ev.Reward?.ParticipationItems, config.rewardParticipationItemCode, config.rewardParticipationQuantity);

        /// <summary>Placement tiers as (1-based place, items), or null when the event has none.</summary>
        public static List<(int place, List<RewardItem> items)> PlacementTiers(CANEvent ev)
        {
            var tiers = ev.Reward?.PlacementRewards;
            if (tiers == null || tiers.Count == 0) return null;

            var result = new List<(int place, List<RewardItem> items)>();
            for (int i = 0; i < tiers.Count; i++)
            {
                var items = tiers[i]?.Items;
                if (items != null && items.Count > 0) result.Add((i + 1, items));
            }
            return result;
        }

        /// <summary>Read-only view of the whole payout for the event browser; placement wins when
        /// configured, otherwise the flat winner set.</summary>
        public static RewardPreview Preview(CANEvent ev, Config config)
        {
            var preview = new RewardPreview();

            var placement = PlacementTiers(ev);
            if (placement != null && placement.Count > 0)
            {
                preview.Placement = placement;
            }
            else
            {
                var winner = WinnerItems(ev, config);
                if (winner.Count > 0) preview.WinnerItems = winner;
            }

            if (ParticipationEnabled(ev, config))
            {
                var part = ParticipationItems(ev, config);
                if (part.Count > 0) preview.ParticipationItems = part;
            }
            return preview;
        }

        private static List<RewardItem> ResolveSet(List<RewardItem> perEvent, string globalCode, int globalQty)
        {
            if (perEvent != null && perEvent.Count > 0) return perEvent;
            if (!string.IsNullOrEmpty(globalCode) && globalQty > 0)
                return new List<RewardItem> { new RewardItem { Code = globalCode, Quantity = globalQty } };
            return new List<RewardItem>();
        }
    }
}
