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
        // Writes its file only on first use.
        private readonly EntryFeeForfeitStore forfeitStore;
        private readonly EntryFeeSettlement entryFees;

        // Payouts for players who are offline when the match ends.
        public PayoutQueue Payouts { get; }

        public RewardDistributor(ICoreServerAPI sapi, Config config, PayoutQueue payouts = null)
        {
            this.sapi = sapi;
            this.config = config;
            Payouts = payouts ?? new PayoutQueue(sapi);
            // Only created when enabled, so no state file otherwise.
            if (config.rewardRateCapBurst > 0)
                rateLimiter = new RewardRateLimiter(sapi, config.rewardRateCapBurst, config.rewardRateCapRefillHours);
            forfeitStore = new EntryFeeForfeitStore(sapi);
            entryFees = new EntryFeeSettlement(sapi, config, forfeitStore, this);
        }

        // Null when disabled.
        public RewardRateLimiter RateLimiter => rateLimiter;

        // After an admin edits rewardRateCap*. Other reward settings are read per match.
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

        private bool UsesOwnRewardBucket(CANEvent ev) => ev.Reward?.SeparateRewardCap ?? config.rewardRateCapPerEvent;

        private bool RateCapActiveFor(CANEvent ev) => rateLimiter != null && !(ev.Reward?.RateCapExempt ?? false);

        private bool ParticipationEnabled(CANEvent ev) => RewardResolver.ParticipationEnabled(ev, config);

        // For the event browser; active=false hides the counter.
        public (bool active, int remaining, double secondsToNext) GetRewardCapStatus(string uid, CANEvent ev)
        {
            if (!RateCapActiveFor(ev)) return (false, 0, 0);
            string capKey = UsesOwnRewardBucket(ev) ? ev.Code : null;
            var (remaining, secondsToNext) = rateLimiter.PeekStatus(uid, capKey, UnixTime.Now);
            return (true, remaining, secondsToNext);
        }

        // Entry fees are handled in EntryFeeSettlement; these just forward.

        // Crash/reconnect recovery when no running event owns the player.
        public void RefundEntryFeeEscrow(IServerPlayer player) => entryFees.RefundEscrow(player);

        // Stake of a mid-match disconnect.
        public void RecordForfeit(string uid, long matchId, List<RewardItem> items, string eventCode = null)
            => entryFees.RecordForfeit(uid, matchId, items, eventCode);

        // Refunds only if the match is no longer running.
        public void RefundForfeitIfStale(IServerPlayer player, System.Func<long, bool> matchStillRunning)
            => entryFees.RefundForfeitIfStale(player, matchStillRunning);

        public void DistributeRewards(RunningEvent runningEvent, MatchResult result)
        {
            var winners = result.Winners;
            var ev = runningEvent.Event;

            // Anti-throw: too-short matches pay nothing. 0 disables.
            if (RewardResolver.BelowContributionGate(ev, config, runningEvent.ElapsedSeconds, out int minSeconds))
            {
                sapi.World.Logger.Notification(
                    "RewardDistributor: no winner reward for {0} - match too short ({1:0}s < {2}s)",
                    ev.Code, runningEvent.ElapsedSeconds, minSeconds);
                foreach (var uid in winners)
                    (sapi.World.PlayerByUid(uid) as IServerPlayer)?.SendMessage(0,
                        Lang.Get("canevents:reward-too-short"), EnumChatType.Notification);
                // No winners = refund all stakes.
                entryFees.Settle(runningEvent, new HashSet<string>());
                // No participation reward either, or the gate is farmable.
                return;
            }

            // Bots in lobby or tournament sub-match: settle stakes, no event reward, unless the event allows bots.
            int botCount = runningEvent.ParticipantsBefore.Count(Bots.BotIdentity.IsBot);
            var botMode = BotRewardModes.Of(ev.Reward);
            if (runningEvent.SuppressRewards || (botCount > 0 && botMode == BotRewardMode.None))
            {
                sapi.World.Logger.Notification(
                    "RewardDistributor: no event reward for {0} - {1}.", ev.Code,
                    runningEvent.SuppressRewards ? "tournament sub-match" : "the lobby contained bots");
                entryFees.Settle(runningEvent, winners);
                return;
            }

            // Only the top reward is rate-capped. capKey null = shared bucket.
            bool rateCapOn = RateCapActiveFor(ev);
            string capKey = UsesOwnRewardBucket(ev) ? ev.Code : null;
            long now = UnixTime.Now;

            int lobby = runningEvent.ParticipantsBefore.Count;
            share = botCount > 0 && botMode == BotRewardMode.Scaled && lobby > 0
                ? (double)(lobby - botCount) / lobby
                : 1.0;
            if (share < 1.0)
                sapi.World.Logger.Notification("RewardDistributor: {0} pays {1:0.##} of its rewards - {2} of {3} were bots.",
                    ev.Code, share, botCount, lobby);

            // Ranking from the MatchResult snapshot; behavior OnEnd may have wiped the live state.
            var placementTiers = ev.Reward?.PlacementRewards;
            var ranking = (placementTiers != null && placementTiers.Count > 0) ? result.Ranking : null;

            HashSet<string> rewarded = (ranking != null && ranking.Count > 0)
                ? DistributePlacement(capKey, ranking, placementTiers, rateCapOn, now)
                : DistributeFlatWinner(ev, capKey, winners, rateCapOn, now);

            entryFees.Settle(runningEvent, winners);
            DistributeParticipation(runningEvent, rewarded, capKey, rateCapOn, now);
            rateLimiter?.Flush();
            share = 1.0;
        }

        // Human share of a lobby with bots, for "scaled" events. Set for the length of one DistributeRewards.
        private double share = 1.0;

        // A fractional unit is paid with that chance, so a 1-item prize at share 0.5 pays every other match on average.
        private List<RewardItem> Scaled(List<RewardItem> items)
        {
            if (share >= 1.0 || items == null) return items;
            var result = new List<RewardItem>();
            foreach (var item in items)
            {
                if (item == null) continue;
                double exact = item.Quantity * share;
                int quantity = (int)Math.Floor(exact);
                if (sapi.World.Rand.NextDouble() < exact - quantity) quantity++;
                if (quantity > 0) result.Add(new RewardItem { Code = item.Code, Quantity = quantity });
            }
            return result;
        }

        // Winners skip participation even if they got nothing.
        private HashSet<string> DistributeFlatWinner(CANEvent ev, string capKey, HashSet<string> winners, bool rateCapOn, long now)
        {
            var rewarded = new HashSet<string>(winners);
            var winnerItems = ResolveWinnerItems(ev);
            if (winnerItems.Count == 0) return rewarded;

            foreach (var winnerUid in winners)
                PayOrQueue(winnerUid, winnerItems, capKey, rateCapOn, now, "reward", ev.Code, "canevents:reward-won");
            return rewarded;
        }

        // Bracket 0 = 1st place, the only one rate-capped.
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
                    if (PayOrQueue(uid, items, capKey, capThisBracket, now, "placement", capKey,
                            "canevents:reward-placement", i + 1))
                        rewarded.Add(uid);
                }
            }
            return rewarded;
        }

        // Cap is checked before the offline queue so logging out doesn't dodge it.
        // True = exclude from participation.
        private bool PayOrQueue(string uid, List<RewardItem> items, string capKey, bool rateCapped, long now,
            string reason, string source, string successLangKey, params object[] msgArgs)
        {
            // Only "allowWithBots" events get here with bots; a bot is never paid or queued.
            if (Bots.BotIdentity.IsBot(uid)) return true;

            var player = sapi.World.PlayerByUid(uid) as IServerPlayer;
            if (rateCapped && !rateLimiter.TryConsume(uid, capKey, now))
            {
                player?.SendMessage(0, Lang.Get("canevents:reward-rate-capped"), EnumChatType.Notification);
                return true;
            }

            items = Scaled(items);
            if (items.Count == 0) return true;
            if (player == null)
            {
                Payouts.Queue(uid, items, reason, source);
                return true;
            }
            if (GiveAll(player, items))
            {
                player.SendMessage(0, Lang.Get(successLangKey, msgArgs), EnumChatType.Notification);
                return true;
            }
            return false;
        }

        // Separate bucket so losses don't eat the winner allowance.
        private void DistributeParticipation(RunningEvent runningEvent, HashSet<string> alreadyRewarded,
            string capKey, bool rateCapOn, long now)
        {
            if (!ParticipationEnabled(runningEvent.Event)) return;

            var items = ResolveParticipationItems(runningEvent.Event);
            if (items.Count == 0) return;

            string bucket = "participation:" + (capKey ?? "*");
            foreach (var plGuid in runningEvent.ParticipantsBefore)
            {
                if (alreadyRewarded.Contains(plGuid)) continue;
                if (runningEvent.Roster.IsPreStartDropout(plGuid)) continue;
                if (runningEvent.LeftInPlay(plGuid)) continue;
                PayOrQueue(plGuid, items, bucket, rateCapOn, now, "participation", capKey,
                    "canevents:reward-participation");
            }
        }

        public RewardPreview GetRewardPreview(CANEvent ev) => RewardResolver.Preview(ev, config);

        private List<RewardItem> ResolveWinnerItems(CANEvent ev) => RewardResolver.WinnerItems(ev, config);

        private List<RewardItem> ResolveParticipationItems(CANEvent ev) => RewardResolver.ParticipationItems(ev, config);

        // True if at least one stack was given.
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

        // False (and logs) for an unknown code.
        public bool GiveReward(IServerPlayer player, string itemCode, int quantity)
            => InventoryUtil.GiveByCode(sapi, player, itemCode, quantity, "reward");
    }

    // Display only. WinnerItems or Placement, not both. Lists are live config refs, don't mutate.
    public sealed class RewardPreview
    {
        public List<RewardItem> WinnerItems;
        public List<(int place, List<RewardItem> items)> Placement;   // place is 1-based
        public List<RewardItem> ParticipationItems;                   // null when participation is off
    }

}
