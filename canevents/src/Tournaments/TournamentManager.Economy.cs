using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers;
using canevents.src.EventManagers.events;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace canevents.src.Tournaments
{
    /// <summary>
    /// The tournament's money. Deliberately independent of EntryFeeEscrow: a player holds only one
    /// escrow slot (locking them out of other paid events), EntryFeeSettlement would pay the stakes
    /// out after round one, and RefundIfNoLongerCommitted would refund entrants between rounds. So the
    /// stake is taken from the inventory at signup into the tournament's own persisted pot; sub-matches
    /// carry SuppressEntryFeeSettlement and never touch it.
    /// </summary>
    public partial class TournamentManager
    {
        /// <summary>
        /// Takes the entry fee: one stake per side, paid by the leader (first uid), so one click never
        /// empties the other members' inventories and the refund goes back to the same person.
        /// </summary>
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

            // Charge, then record. The gap is the same accepted risk EntryFeeEscrow carries; the
            // removal is logged so a crash in between is reconstructible.
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

        /// <summary>Hands back (and removes from the pot) everything these players put in.</summary>
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

        /// <summary>
        /// Pays the finished bracket out: pot by placement percentage plus per-place item prizes. A team
        /// splits its pot share but every member gets the full item set, as RewardTier does.
        /// </summary>
        private void PayOut(Tournament t)
        {
            if (t.FinalPlacement.Count == 0) return;

            // Only placements with somebody to pay count: every coin came out of a player's inventory,
            // so paying a share to a bot would burn real property.
            int payableCount = t.FinalPlacement.Count(id => HumanMembers(t, id).Count > 0);
            var shares = TournamentPot.Split(t.Pot, t.RakePercent, t.PotSplitPercents, payableCount);

            // Worked out in full before anything is handed out, so emptying the pot and owing every
            // share is one atomic write; a crash after it loses nothing, the rows are persisted.
            var payouts = new List<PendingPayout>();
            void Owe(string uid, List<RewardItem> items)
            {
                if (items == null || items.Count == 0) return;
                // A bot has no inventory, and a parked row for one would never be collected.
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

                // Item prizes follow the REAL placement, not the payout order: second place gets the
                // second prize even if the winner was a bot nobody could pay.
                if (t.PlacementPrizes != null && place < t.PlacementPrizes.Count)
                {
                    var prize = t.PlacementPrizes[place]?.Items;
                    if (prize != null && prize.Count > 0)
                        foreach (var uid in members)
                            Owe(uid, prize);
                }
            }

            store.Mutate(d =>
            {
                t.Pot.Clear();
                t.Contributions.Clear();
                foreach (var p in payouts) d.Pending.Add(p);
            });

            // Books straight: hand over to whoever is online, the rest collect on next login.
            foreach (var uid in payouts.Select(p => p.Uid).Distinct())
                if (sapi.World.PlayerByUid(uid) is IServerPlayer online)
                    DeliverPendingPayouts(online);
        }

        /// <summary>
        /// Grants an item set now, or parks it for delivery on reconnect. Bypasses the anti-farm rate
        /// cap on purpose: stakes are the players' own property and prizes are admin-run.
        /// </summary>
        private void Deliver(Tournament t, string uid, List<RewardItem> items, string reason)
        {
            if (items == null || items.Count == 0) return;
            // A bot has no inventory, and a parked row for one would never be collected.
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

        /// <summary>
        /// Hands over anything owed to a returning player. The rows are taken out of the store first
        /// (they must be, or a second login would pay twice), so a payout that throws half-way is put
        /// back rather than lost: the prize outlives the failure and is tried again next login.
        /// </summary>
        private void DeliverPendingPayouts(IServerPlayer player)
        {
            var owed = store.TakePayouts(player.PlayerUID);
            if (owed.Count == 0) return;

            foreach (var p in owed)
            {
                try
                {
                    foreach (var it in p.Items)
                        InventoryUtil.GiveByCode(sapi, player, it.Code, it.Quantity, "tournament " + p.Reason);
                }
                catch (Exception e)
                {
                    log.Error("payout for {0} ({1}) failed, re-queued: {2}", player.PlayerName, p.Reason, e.Message);
                    store.QueuePayout(p);
                }
            }

            player.SendMessage(0, Lang.Get("canevents:tournament-payout-delivered"), EnumChatType.Notification);
        }

        // small helpers

        /// <summary>The members of a side that can actually receive anything - bots cannot.</summary>
        private static List<string> HumanMembers(Tournament t, string entrantId)
        {
            var e = t.FindEntrant(entrantId);
            if (e?.Uids == null) return new List<string>();
            return e.Uids.Where(uid => !Bots.BotIdentity.IsBot(uid)).ToList();
        }

        private static void AddToPot(Dictionary<string, int> pot, IEnumerable<RewardItem> items)
        {
            foreach (var it in items)
            {
                if (string.IsNullOrEmpty(it.Code) || it.Quantity <= 0) continue;
                pot[it.Code] = (pot.TryGetValue(it.Code, out int had) ? had : 0) + it.Quantity;
            }
        }

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

        private static List<RewardItem> CloneItems(List<RewardItem> items)
            => items.Select(i => new RewardItem { Code = i.Code, Quantity = i.Quantity }).ToList();

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
