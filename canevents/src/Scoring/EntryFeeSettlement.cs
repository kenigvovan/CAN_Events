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
    public interface IRewardPayout
    {
        bool GiveAll(IServerPlayer player, List<RewardItem> items);
        bool GiveReward(IServerPlayer player, string itemCode, int quantity);
    }

    // Stakes are the players' own items, so unlike rewards they are never rate-capped.
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

        public void RefundEscrow(IServerPlayer player)
        {
            var stake = EntryFeeEscrow.Read(player);
            if (stake == null)
            {
                // Corrupt escrow: clear it anyway, or the player is locked out of paid queues forever.
                if (EntryFeeEscrow.Has(player))
                {
                    EntryFeeEscrow.Clear(player);
                    sapi.World.Logger.Warning(
                        "[canevents] Cleared an unreadable entry-fee escrow for {0}; the stake could not "
                        + "be returned, but they are no longer blocked from paid queues.", player.PlayerName);
                }
                return;
            }
            EntryFeeEscrow.Clear(player);
            RefundItems(player, stake);
        }

        public void RecordForfeit(string uid, long matchId, List<RewardItem> items, string eventCode = null)
            => forfeitStore.Record(uid, matchId, items, eventCode);

        // System.Func: Vintagestory.API.Common has its own Func delegate.
        public void RefundForfeitIfStale(IServerPlayer player, System.Func<long, bool> matchStillRunning)
        {
            if (player == null) return;
            var rec = forfeitStore.Peek(player.PlayerUID);
            // Match ids can repeat after a restart, so older records are always stale.
            if (rec == null) return;
            if (forfeitStore.IsFromThisRun(rec) && (matchStillRunning?.Invoke(rec.MatchId) ?? false)) return;
            forfeitStore.Remove(player.PlayerUID);
            RefundItems(player, rec.Items);
        }

        // Pot = online payers' escrow + forfeits. Offline players are refunded on reconnect.
        // No paying winner (incl. void) refunds everyone.
        public void Settle(RunningEvent re, HashSet<string> winners)
        {
            // Tournament pot is settled once at the end of the bracket.
            if (re.SuppressEntryFeeSettlement) return;
            if (re.Event.EntryFee == null) return;

            var payers = new List<IServerPlayer>();
            var pot = new Dictionary<string, int>();
            foreach (var uid in re.ParticipantsBefore)
            {
                if (!(sapi.World.PlayerByUid(uid) is IServerPlayer player)) continue; // offline: refunded on reconnect
                var stake = EntryFeeEscrow.Read(player);
                if (stake == null) continue;
                // Escrow may belong to another event (admin pulled the player in from a different queue).
                if (!re.Event.CodeEquals(EntryFeeEscrow.EventCodeOf(player))) continue;
                payers.Add(player);
                AddToPot(pot, stake);
            }

            var forfeits = forfeitStore.ForMatch(re.Id, re.Event.Code);
            if (payers.Count == 0 && forfeits.Count == 0) return;

            var winnerPayers = payers.Where(p => winners != null && winners.Contains(p.PlayerUID)).ToList();

            // Draw, or the winner didn't pay.
            if (winnerPayers.Count == 0)
            {
                foreach (var p in payers) RefundEscrow(p);
                ResolveForfeitsVoid(forfeits);
                return;
            }

            // Forfeits join the pot so disconnecting doesn't dodge the wager.
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

            foreach (var p in payers) EntryFeeEscrow.Clear(p);

            if (paidAnything)
                foreach (var p in winnerPayers)
                    p.SendMessage(0, Lang.Get("canevents:entryfee-pot-won"), EnumChatType.Notification);
        }

        // Without entryFeeRefundForfeitOnVoid the stake is burned.
        private void ResolveForfeitsVoid(List<(string uid, ForfeitRecord rec)> forfeits)
        {
            foreach (var (uid, rec) in forfeits)
            {
                if (!config.entryFeeRefundForfeitOnVoid)
                {
                    forfeitStore.Remove(uid);
                    continue;
                }
                var pl = sapi.World.PlayerByUid(uid) as IServerPlayer;
                if (pl == null) continue; // refunded on reconnect
                RefundItems(pl, rec.Items);
                forfeitStore.Remove(uid);
            }
        }

        // Caller clears the escrow/forfeit record.
        private void RefundItems(IServerPlayer player, List<RewardItem> items)
        {
            if (player == null || items == null) return;
            payout.GiveAll(player, items);
            player.SendMessage(0, Lang.Get("canevents:entryfee-refunded"), EnumChatType.Notification);
        }

        private static void AddToPot(Dictionary<string, int> pot, IEnumerable<RewardItem> items)
            => EntryFeePot.AddToPot(pot, items);
    }
}
