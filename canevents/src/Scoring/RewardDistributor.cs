using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers;
using canevents.src.EventManagers.events;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace canevents.src.Scoring
{
    public class RewardDistributor : IRewardPayout
    {
        private ICoreServerAPI sapi;
        private Config config;
        private RewardRateLimiter rateLimiter;
        // Always present - it writes a file only on first use.
        private readonly EntryFeeForfeitStore forfeitStore;
        private readonly EntryFeeSettlement entryFees;

        /// <summary>Where a payout goes when its owner is not online. Rewards land at the very end of
        /// a match, which is exactly when a beaten player has often already quit.</summary>
        public PayoutQueue Payouts { get; }

        public RewardDistributor(ICoreServerAPI sapi, Config config, PayoutQueue payouts = null)
        {
            this.sapi = sapi;
            this.config = config;
            Payouts = payouts ?? new PayoutQueue(sapi);
            // Anti-farm rate-cap: instantiated only when enabled, so disabled servers create no state file.
            if (config.rewardRateCapBurst > 0)
                rateLimiter = new RewardRateLimiter(sapi, config.rewardRateCapBurst, config.rewardRateCapRefillHours);
            forfeitStore = new EntryFeeForfeitStore(sapi);
            entryFees = new EntryFeeSettlement(sapi, config, forfeitStore, this);
        }

        /// <summary>Exposes the rate limiter for admin commands (peek/reset). Null when disabled.</summary>
        public RewardRateLimiter RateLimiter => rateLimiter;

        /// <summary>
        /// Re-applies the rate-cap after an admin edit of the rewardRateCap* config fields. Other reward
        /// settings are read from config per match, so they need no apply step.
        /// </summary>
        public void ReconfigureRateCap()
        {
            if (config.rewardRateCapBurst > 0)
            {
                if (rateLimiter == null)
                    rateLimiter = new RewardRateLimiter(sapi, config.rewardRateCapBurst, config.rewardRateCapRefillHours);
                else
                    rateLimiter.Reconfigure(config.rewardRateCapBurst, config.rewardRateCapRefillHours);
            }
            else rateLimiter = null;
        }

        // Whether this event draws from its OWN rate-cap bucket (per-event flag, else the global default).
        private bool UsesOwnRewardBucket(CANEvent ev) => ev.Reward?.SeparateRewardCap ?? config.rewardRateCapPerEvent;

        // Whether the winner-reward rate-cap governs this event (limiter enabled AND event not exempt).
        private bool RateCapActiveFor(CANEvent ev) => rateLimiter != null && !(ev.Reward?.RateCapExempt ?? false);

        // Whether participants (non-winners) get a reward (per-event flag, else the global default).
        private bool ParticipationEnabled(CANEvent ev) => RewardResolver.ParticipationEnabled(ev, config);

        /// <summary>
        /// Per-viewer winner-reward cap status for the event browser. active=false (cap disabled or event
        /// RateCapExempt) means the UI shows no counter; otherwise the figures describe the relevant
        /// bucket - this event's own when it has a separate cap, else the shared one.
        /// </summary>
        public (bool active, int remaining, double secondsToNext) GetRewardCapStatus(string uid, CANEvent ev)
        {
            if (!RateCapActiveFor(ev)) return (false, 0, 0);
            string capKey = UsesOwnRewardBucket(ev) ? ev.Code : null;
            var (remaining, secondsToNext) = rateLimiter.PeekStatus(uid, capKey, UnixTime.Now);
            return (true, remaining, secondsToNext);
        }

        // Wagers: the entry-fee side lives in EntryFeeSettlement; these stay as the callers' entry points.

        /// <summary>
        /// Returns a player's entry-fee escrow and clears it. Used by crash/reconnect recovery when no
        /// running event owns the player: the wager is voided and refunded.
        /// </summary>
        public void RefundEntryFeeEscrow(IServerPlayer player) => entryFees.RefundEscrow(player);

        /// <summary>
        /// Parks the stake of a mid-match disconnect (read from escrow by the caller, which still holds
        /// the player object). See <see cref="EntryFeeForfeitStore"/>.
        /// </summary>
        public void RecordForfeit(string uid, long matchId, List<RewardItem> items)
            => entryFees.RecordForfeit(uid, matchId, items);

        /// <summary>
        /// Reconnect recovery for a parked forfeit: hand the stake back if its match is no longer running
        /// (crashed before settling, or settled void). A still-running match keeps it committed.
        /// </summary>
        public void RefundForfeitIfStale(IServerPlayer player, System.Func<long, bool> matchStillRunning)
            => entryFees.RefundForfeitIfStale(player, matchStillRunning);

        public void DistributeRewards(RunningEvent runningEvent, MatchResult result)
        {
            var winners = result.Winners;
            var ev = runningEvent.Event;

            // Contribution-gate (anti-throw): a match shorter than the threshold pays no winner/placement
            // reward, so instant-throw win-trading earns nothing. 0 (or unset → global) disables it.
            if (RewardResolver.BelowContributionGate(ev, config, runningEvent.ElapsedSeconds, out int minSeconds))
            {
                sapi.World.Logger.Notification(
                    "RewardDistributor: no winner reward for {0} - match too short ({1:0}s < {2}s)",
                    ev.Code, runningEvent.ElapsedSeconds, minSeconds);
                foreach (var uid in winners)
                    (sapi.World.PlayerByUid(uid) as IServerPlayer)?.SendMessage(0,
                        Lang.Get("canevents:reward-too-short"), EnumChatType.Notification);
                // Void: passing no winners refunds every escrow instead of paying a pot, so an instant
                // throw can't grab the stakes.
                entryFees.Settle(runningEvent, new HashSet<string>());
                // No participation reward either: paying it here made the gate farmable (queue, throw
                // instantly, collect, repeat).
                return;
            }

            // Settle stakes but pay no event reward when: the lobby had bots (beating a side that can't
            // fight back is the cheapest farm, and standings already skip it), or this is a tournament
            // sub-match (the bracket pays once at the end; per-round paying multiplied the prize).
            if (runningEvent.SuppressRewards || runningEvent.ParticipantsBefore.Any(Bots.BotIdentity.IsBot))
            {
                sapi.World.Logger.Notification(
                    "RewardDistributor: no event reward for {0} - {1}.", ev.Code,
                    runningEvent.SuppressRewards ? "tournament sub-match" : "the lobby contained bots");
                entryFees.Settle(runningEvent, winners);
                return;
            }

            // Rate-cap (anti-farm): only the top reward (1st place / flat winners) is throttled, never
            // lower-place consolation. capKey picks the bucket: this event's own, or null for the shared.
            bool rateCapOn = RateCapActiveFor(ev);
            string capKey = UsesOwnRewardBucket(ev) ? ev.Code : null;
            long now = UnixTime.Now;

            // Placement mode when the event can be ranked, else a flat reward to every winner; either way
            // the returned set is skipped by participation. Ranking must come from the pre-cleanup
            // MatchResult snapshot - behavior OnEnd may have wiped the live ranking state.
            var placementTiers = ev.Reward?.PlacementRewards;
            var ranking = (placementTiers != null && placementTiers.Count > 0) ? result.Ranking : null;

            HashSet<string> rewarded = (ranking != null && ranking.Count > 0)
                ? DistributePlacement(capKey, ranking, placementTiers, rateCapOn, now)
                : DistributeFlatWinner(ev, capKey, winners, rateCapOn, now);

            entryFees.Settle(runningEvent, winners);
            DistributeParticipation(runningEvent, rewarded, capKey, rateCapOn, now);
            rateLimiter?.Flush();
        }

        // Flat winner reward: the same item set to every winner. Winners are excluded from participation
        // even if they got nothing (capped / no reward configured).
        private HashSet<string> DistributeFlatWinner(CANEvent ev, string capKey, HashSet<string> winners, bool rateCapOn, long now)
        {
            var rewarded = new HashSet<string>(winners);
            var winnerItems = ResolveWinnerItems(ev);
            if (winnerItems.Count == 0) return rewarded;

            foreach (var winnerUid in winners)
            {
                var player = sapi.World.PlayerByUid(winnerUid) as IServerPlayer;
                if (player == null)
                {
                    // Offline: the win is theirs whether or not they stayed to watch the results.
                    Payouts.Queue(winnerUid, winnerItems, "reward", ev.Code);
                    continue;
                }
                PayWinner(player, winnerUid, capKey, winnerItems, rateCapOn, now, "canevents:reward-won");
            }
            return rewarded;
        }

        // Bracket i (0 = 1st place) grants PlacementRewards[i] to every player in that bracket.
        // Only the top bracket (the actual win) is rate-capped. Returns who received a reward.
        private HashSet<string> DistributePlacement(string capKey, List<HashSet<string>> ranking, List<RewardTier> tiers,
            bool rateCapOn, long now)
        {
            var rewarded = new HashSet<string>();
            int brackets = Math.Min(ranking.Count, tiers.Count);
            for (int i = 0; i < brackets; i++)
            {
                var items = tiers[i]?.Items;
                if (items == null || items.Count == 0) continue;
                bool capThisBracket = rateCapOn && i == 0;
                foreach (var uid in ranking[i])
                {
                    var player = sapi.World.PlayerByUid(uid) as IServerPlayer;
                    if (player == null)
                    {
                        Payouts.Queue(uid, items, "placement", capKey);
                        rewarded.Add(uid);   // paid (later) counts as paid: no participation on top
                        continue;
                    }
                    if (PayWinner(player, uid, capKey, items, capThisBracket, now, "canevents:reward-placement", i + 1))
                        rewarded.Add(uid);
                }
            }
            return rewarded;
        }

        // Applies the rate-cap against the given bucket key, then grants the item set. Returns true if the
        // player should be excluded from participation (won, whether actually paid or denied by the cap).
        private bool PayWinner(IServerPlayer player, string uid, string capKey, List<RewardItem> items,
            bool rateCapped, long now, string successLangKey, params object[] msgArgs)
        {
            if (rateCapped && !rateLimiter.TryConsume(uid, capKey, now))
            {
                player.SendMessage(0, Lang.Get("canevents:reward-rate-capped"), EnumChatType.Notification);
                return true; // won but hit the cap → still excluded from participation
            }
            if (GiveAll(player, items))
            {
                player.SendMessage(0, Lang.Get(successLangKey, msgArgs), EnumChatType.Notification);
                return true;
            }
            return false;
        }

        // Participation draws from its own rate-cap bucket: deliberate repeat losing needs a ceiling of
        // its own, and sharing the winner bucket would let a loss eat a legitimate win's allowance.
        private void DistributeParticipation(RunningEvent runningEvent, HashSet<string> alreadyRewarded,
            string capKey, bool rateCapOn, long now)
        {
            if (!ParticipationEnabled(runningEvent.Event)) return;

            var items = ResolveParticipationItems(runningEvent.Event);
            if (items.Count == 0) return;

            string bucket = "participation:" + (capKey ?? "*");
            foreach (var plGuid in runningEvent.ParticipantsBefore)
            {
                if (alreadyRewarded.Contains(plGuid)) continue; // already got a winner/placement reward
                var player = sapi.World.PlayerByUid(plGuid) as IServerPlayer;
                if (player == null)
                {
                    // Not rate-capped on the way into the queue: the cap is a live-play throttle, and
                    // charging it here would spend the allowance of someone who is not even playing.
                    Payouts.Queue(plGuid, items, "participation", capKey);
                    continue;
                }
                if (rateCapOn && !rateLimiter.TryConsume(plGuid, bucket, now))
                {
                    player.SendMessage(0, Lang.Get("canevents:reward-rate-capped"), EnumChatType.Notification);
                    continue;
                }
                if (GiveAll(player, items))
                    player.SendMessage(0, Lang.Get("canevents:reward-participation"), EnumChatType.Notification);
            }
        }

        /// <summary>
        /// Read-only view of what an event pays out, for the event browser. Matches the actual payout
        /// because both go through <see cref="RewardResolver"/>.
        /// </summary>
        public RewardPreview GetRewardPreview(CANEvent ev) => RewardResolver.Preview(ev, config);

        private List<RewardItem> ResolveWinnerItems(CANEvent ev) => RewardResolver.WinnerItems(ev, config);

        private List<RewardItem> ResolveParticipationItems(CANEvent ev) => RewardResolver.ParticipationItems(ev, config);

        // Returns true if at least one stack was actually given. Public via IRewardPayout: the wager
        // settlement hands back stakes through the same path.
        public bool GiveAll(IServerPlayer player, List<RewardItem> items)
        {
            bool gaveAnything = false;
            foreach (var item in items)
            {
                if (item == null || item.Quantity <= 0) continue;
                gaveAnything |= GiveReward(player, item.Code, item.Quantity);
            }
            return gaveAnything;
        }

        // Returns false (and logs) when the code matches neither an item nor a block, so callers don't
        // announce a reward that wasn't actually given.
        public bool GiveReward(IServerPlayer player, string itemCode, int quantity)
            => InventoryUtil.GiveByCode(sapi, player, itemCode, quantity, "reward");
    }

    /// <summary>Resolved payout of an event for display only. Exactly one of <see cref="WinnerItems"/>
    /// / <see cref="Placement"/> is populated (placement wins when configured); both null when the
    /// event has no winner reward. Lists are the live config references - do not mutate.</summary>
    public sealed class RewardPreview
    {
        public List<RewardItem> WinnerItems;                          // flat winner set (no placement tiers)
        public List<(int place, List<RewardItem> items)> Placement;   // per-rank tiers (1-based place)
        public List<RewardItem> ParticipationItems;                   // null when participation is off
    }

}
