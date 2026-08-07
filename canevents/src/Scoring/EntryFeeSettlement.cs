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
    /// <summary>Hands items to a player. Implemented by <see cref="RewardDistributor"/>, which owns the
    /// item lookup and the inventory/drop fallback.</summary>
    public interface IRewardPayout
    {
        bool GiveAll(IServerPlayer player, List<RewardItem> items);
        bool GiveReward(IServerPlayer player, string itemCode, int quantity);
    }

    /// <summary>
    /// The wager side of a match: stakes charged at sign-up, parked on mid-match disconnect, settled into
    /// a pot (or refunded) at the end. Split from <see cref="RewardDistributor"/> because rewards are
    /// minted by the server and rate-capped, while stakes are the players' own and never capped.
    /// </summary>
    public class EntryFeeSettlement
    {
        private readonly ICoreServerAPI sapi;
        private readonly Config config;
        private readonly EntryFeeForfeitStore forfeitStore;
        private readonly IRewardPayout payout;

        public EntryFeeSettlement(ICoreServerAPI sapi, Config config, EntryFeeForfeitStore forfeitStore,
            IRewardPayout payout)
        {
            this.sapi = sapi;
            this.config = config;
            this.forfeitStore = forfeitStore;
            this.payout = payout;
        }

        /// <summary>
        /// Returns a player's escrow and clears it. Used by crash/reconnect recovery when no running
        /// event owns the player: the wager is voided and refunded.
        /// </summary>
        public void RefundEscrow(IServerPlayer player)
        {
            var stake = EntryFeeEscrow.Read(player);
            if (stake == null) return;
            EntryFeeEscrow.Clear(player);
            RefundItems(player, stake);
        }

        /// <summary>
        /// Parks the stake of a mid-match disconnect (read from escrow by the caller, which still holds
        /// the player object). See <see cref="EntryFeeForfeitStore"/>.
        /// </summary>
        public void RecordForfeit(string uid, long matchId, List<RewardItem> items)
            => forfeitStore.Record(uid, matchId, items);

        /// <summary>
        /// Reconnect recovery for a parked forfeit: hand the stake back if its match is no longer running
        /// (crashed before settling, or settled void). A still-running match keeps it committed.
        /// </summary>
        // System.Func explicitly: Vintagestory.API.Common declares its own Func delegate, which the
        // using above would otherwise bind this parameter to.
        public void RefundForfeitIfStale(IServerPlayer player, System.Func<long, bool> matchStillRunning)
        {
            if (player == null) return;
            var rec = forfeitStore.Peek(player.PlayerUID);
            if (rec == null || (matchStillRunning?.Invoke(rec.MatchId) ?? false)) return;
            forfeitStore.Remove(player.PlayerUID);
            RefundItems(player, rec.Items);
        }

        /// <summary>
        /// Settles queue-time escrow. The pot is the ONLINE participants' escrows plus stakes forfeited by
        /// mid-match disconnects; players offline for other reasons (e.g. a server crash) are refunded on
        /// reconnect instead. The post-rake pot goes to the winners who actually paid; with no paying
        /// winner every online payer is refunded and forfeits go through <see cref="ResolveForfeitsVoid"/>.
        /// Exempt from the anti-farm rate-cap (the items are the players' own). A void result arrives with
        /// no winners and lands in the refund branch, so an instant throw can't grab the pot.
        /// </summary>
        public void Settle(RunningEvent re, HashSet<string> winners)
        {
            // Stakes owned by something outside the match (a tournament pot settled once at the end of
            // the bracket) are none of this match's business.
            if (re.SuppressEntryFeeSettlement) return;
            if (re.Event.EntryFee == null) return;

            var payers = new List<IServerPlayer>();
            var pot = new Dictionary<string, int>();
            foreach (var uid in re.ParticipantsBefore)
            {
                if (!(sapi.World.PlayerByUid(uid) is IServerPlayer player)) continue; // offline: refunded on reconnect
                var stake = EntryFeeEscrow.Read(player);
                if (stake == null) continue; // didn't pay → not pot-eligible
                // Only a stake committed to THIS event counts: a player carries one escrow slot, which may
                // belong to another event (queued for one, pulled into another by an admin start), and
                // pooling it here would pay somebody else's wager to this match's winner.
                if (!re.Event.CodeEquals(EntryFeeEscrow.EventCodeOf(player))) continue;
                payers.Add(player);
                AddToPot(pot, stake);
            }

            var forfeits = forfeitStore.ForMatch(re.Id);
            if (payers.Count == 0 && forfeits.Count == 0) return;

            var winnerPayers = payers.Where(p => winners != null && winners.Contains(p.PlayerUID)).ToList();

            // No paying winner (draw, or the winner didn't ante) → refund payers, resolve forfeits as void.
            if (winnerPayers.Count == 0)
            {
                foreach (var p in payers) RefundEscrow(p);
                ResolveForfeitsVoid(forfeits);
                return;
            }

            // Real result: forfeited stakes join the pot so the win pays the full wager (anti-dodge).
            foreach (var (uid, rec) in forfeits)
            {
                AddToPot(pot, rec.Items);
                forfeitStore.Remove(uid);
            }

            bool paidAnything = false;
            var shares = EntryFeePot.Split(pot, re.Event.EntryFee.RakePercent, winnerPayers.Count);
            foreach (var kv in shares)
            {
                for (int i = 0; i < winnerPayers.Count; i++)
                {
                    int give = kv.Value[i];
                    if (give > 0) paidAnything |= payout.GiveReward(winnerPayers[i], kv.Key, give);
                }
            }

            // Consume every online payer's escrow, winners and losers alike: the pot has been paid.
            foreach (var p in payers) EntryFeeEscrow.Clear(p);

            if (paidAnything)
                foreach (var p in winnerPayers)
                    p.SendMessage(0, Lang.Get("canevents:entryfee-pot-won"), EnumChatType.Notification);
        }

        // Forfeited stakes when the match ends void. Under the refund policy an online forfeiter is paid
        // back now, an offline one keeps its record and is refunded on reconnect; otherwise it is burned.
        private void ResolveForfeitsVoid(List<(string uid, ForfeitRecord rec)> forfeits)
        {
            foreach (var (uid, rec) in forfeits)
            {
                if (!config.entryFeeRefundForfeitOnVoid)
                {
                    forfeitStore.Remove(uid); // burned
                    continue;
                }
                var pl = sapi.World.PlayerByUid(uid) as IServerPlayer;
                if (pl == null) continue; // offline → leave parked; refunded on reconnect
                RefundItems(pl, rec.Items);
                forfeitStore.Remove(uid);
            }
        }

        // Hands staked items back with the standard "refunded" notice. The caller owns clearing whatever
        // held the stake (escrow ModData or the forfeit store).
        private void RefundItems(IServerPlayer player, List<RewardItem> items)
        {
            if (player == null || items == null) return;
            payout.GiveAll(player, items);
            player.SendMessage(0, Lang.Get("canevents:entryfee-refunded"), EnumChatType.Notification);
        }

        private static void AddToPot(Dictionary<string, int> pot, IEnumerable<RewardItem> items)
        {
            if (items == null) return;
            foreach (var item in items)
                if (!string.IsNullOrEmpty(item.Code) && item.Quantity > 0)
                    pot[item.Code] = (pot.TryGetValue(item.Code, out var q) ? q : 0) + item.Quantity;
        }
    }
}
