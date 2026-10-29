using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers;
using canevents.src.EventManagers.events;
using canevents.src.Scoring;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace canevents.src.Tournaments
{
    // Own pot, not EntryFeeEscrow: escrow is one slot per player and would settle/refund per round.
    // Sub-matches run with SuppressEntryFeeSettlement.
    public partial class TournamentManager
    {
        // One stake per side, paid by the leader (first uid).
        private bool ChargeEntryFee(Tournament t, List<string> uids, out string error)
        {
            error = null;
            var fee = ItemSetUtil.Coalesce(t.EntryFee);
            if (fee.Count == 0) return true;
            if (uids == null || uids.Count == 0) return true;

            string leaderUid = uids[0];
            if (!(sapi.World.PlayerByUid(leaderUid) is IServerPlayer leader))
            {
                error = Lang.Get("canevents:tournament-signup-offline", NameOf(leaderUid));
                return false;
            }
            if (!InventoryUtil.Has(leader, fee))
            {
                error = Lang.Get("canevents:tournament-fee-missing", DescribeItems(t.EntryFee), leader.PlayerName);
                return false;
            }

            // Charge then record, logged in case of a crash in between (same as EntryFeeEscrow).
            var taken = new List<RewardItem>();
            foreach (var (code, qty) in fee)
            {
                int removed = InventoryUtil.Remove(leader, code, qty);
                if (removed > 0) taken.Add(new RewardItem { Code = code, Quantity = removed });
            }
            log.Notification("{0}: charged {1} entry fee {2}",
                t.Id, leader.PlayerName, ItemSetUtil.Pack(taken));

            t.Contributions.Add(new PotContribution { Uid = leader.PlayerUID, Items = taken });
            AddToPot(t.Pot, taken);
            return true;
        }

        private void RefundContributions(Tournament t, IEnumerable<string> uids)
        {
            var set = new HashSet<string>(uids);
            for (int i = t.Contributions.Count - 1; i >= 0; i--)
            {
                var c = t.Contributions[i];
                if (!set.Contains(c.Uid)) continue;
                RemoveFromPot(t.Pot, c.Items);
                Deliver(t, c.Uid, c.Items, "refund");
                t.Contributions.RemoveAt(i);
            }
        }

        private void RefundAllContributions(Tournament t)
        {
            foreach (var c in t.Contributions.ToList())
            {
                RemoveFromPot(t.Pot, c.Items);
                Deliver(t, c.Uid, c.Items, "refund");
            }
            t.Contributions.Clear();
            t.Pot.Clear();
        }

        // Team splits the pot share, but each member gets the full item prize. No side effects,
        // the caller writes it in the same store write that closes the tournament.
        private List<PendingPayout> BuildPayouts(Tournament t)
        {
            var payouts = new List<PendingPayout>();
            if (t.FinalPlacement.Count == 0) return payouts;

            // Bots don't get a pot share.
            int payableCount = t.FinalPlacement.Count(id => HumanMembers(t, id).Count > 0);
            var shares = TournamentPot.Split(t.Pot, t.RakePercent, t.PotSplitPercents, payableCount);

            void Owe(string uid, List<RewardItem> items)
            {
                if (items == null || items.Count == 0) return;
                if (Bots.BotIdentity.IsBot(uid)) return;
                payouts.Add(new PendingPayout
                {
                    Uid = uid,
                    Items = CloneItems(items),
                    TournamentId = t.Id,
                    QueuedUnix = Now,
                    Reason = "prize"
                });
            }

            int payIndex = 0;
            for (int place = 0; place < t.FinalPlacement.Count; place++)
            {
                var members = HumanMembers(t, t.FinalPlacement[place]);
                if (members.Count == 0) continue;

                if (payIndex < shares.Count && shares[payIndex].Count > 0)
                {
                    var perMember = TournamentPot.SplitAmongMembers(shares[payIndex], members.Count);
                    for (int i = 0; i < members.Count; i++)
                        Owe(members[i], ToItems(perMember[i]));
                }
                payIndex++;

                // Item prizes go by real placement, not payout order.
                if (t.PlacementPrizes != null && place < t.PlacementPrizes.Count)
                {
                    var prize = t.PlacementPrizes[place]?.Items;
                    if (prize != null && prize.Count > 0)
                        foreach (var uid in members)
                            Owe(uid, prize);
                }
            }

            return payouts;
        }

        private void DeliverToOnline(IEnumerable<PendingPayout> payouts)
        {
            foreach (var uid in payouts.Select(p => p.Uid).Distinct())
                if (sapi.World.PlayerByUid(uid) is IServerPlayer online)
                    DeliverPendingPayouts(online);
        }

        // Gives now or queues for reconnect. Skips the reward rate limiter.
        private void Deliver(Tournament t, string uid, List<RewardItem> items, string reason)
        {
            if (items == null || items.Count == 0) return;
            if (Bots.BotIdentity.IsBot(uid)) return;

            if (sapi.World.PlayerByUid(uid) is IServerPlayer player)
            {
                foreach (var it in items)
                    InventoryUtil.GiveByCode(sapi, player, it.Code, it.Quantity, "tournament " + reason);
                player.SendMessage(0, reason == "refund"
                    ? Lang.Get("canevents:tournament-refunded", t.Name)
                    : Lang.Get("canevents:tournament-prize-paid", t.Name), EnumChatType.Notification);
                return;
            }

            store.QueuePayout(new PendingPayout
            {
                Uid = uid,
                Items = CloneItems(items),
                TournamentId = t.Id,
                QueuedUnix = Now,
                Reason = reason
            });
        }

        // Rows are taken out first to avoid double pay; a failed payout re-queues the remainder.
        private void DeliverPendingPayouts(IServerPlayer player)
        {
            var owed = store.TakePayouts(player.PlayerUID);
            if (owed.Count == 0) return;

            foreach (var p in owed)
            {
                if (p.Items == null) continue;
                if (PayoutDelivery.TryGiveAll(sapi, player, p.Items, "tournament " + p.Reason, out int given, out var e))
                    continue;

                log.Error("payout for {0} ({1}) failed after {2} of {3} item(s), rest re-queued: {4}",
                    player.PlayerName, p.Reason, given, p.Items.Count, e.Message);
                store.QueuePayout(new PendingPayout
                {
                    Uid = p.Uid,
                    Items = PayoutDelivery.Remainder(p.Items, given),
                    TournamentId = p.TournamentId,
                    QueuedUnix = p.QueuedUnix,
                    Reason = p.Reason
                });
            }

            player.SendMessage(0, Lang.Get("canevents:tournament-payout-delivered"), EnumChatType.Notification);
        }

        private static List<string> HumanMembers(Tournament t, string entrantId)
        {
            var e = t.FindEntrant(entrantId);
            if (e?.Uids == null) return new List<string>();
            return e.Uids.Where(uid => !Bots.BotIdentity.IsBot(uid)).ToList();
        }

        private static void AddToPot(Dictionary<string, int> pot, IEnumerable<RewardItem> items)
            => EntryFeePot.AddToPot(pot, items);

        private static void RemoveFromPot(Dictionary<string, int> pot, IEnumerable<RewardItem> items)
        {
            foreach (var it in items)
            {
                if (string.IsNullOrEmpty(it.Code) || !pot.TryGetValue(it.Code, out int had)) continue;
                int left = had - it.Quantity;
                if (left > 0) pot[it.Code] = left;
                else pot.Remove(it.Code);
            }
        }

        private static List<RewardItem> ToItems(Dictionary<string, int> map)
            => map.Select(kv => new RewardItem { Code = kv.Key, Quantity = kv.Value }).ToList();

        private static List<RewardItem> CloneItems(List<RewardItem> items) => PayoutDelivery.Clone(items);

        internal static string DescribeItems(IEnumerable<RewardItem> items)
        {
            var parts = items?.Where(i => i != null && i.Quantity > 0)
                .Select(i => i.Code + " x" + i.Quantity).ToList();
            return parts == null || parts.Count == 0 ? "-" : string.Join(", ", parts);
        }

        internal static string DescribePot(IReadOnlyDictionary<string, int> pot)
        {
            if (pot == null || pot.Count == 0) return "-";
            return string.Join(", ", pot.Select(kv => kv.Key + " x" + kv.Value));
        }
    }
}
