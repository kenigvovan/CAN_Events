using System;
using System.Collections.Generic;
using canevents.src.EventManagers.events;
using Vintagestory.API.Server;

namespace canevents.src.Scoring
{
    /// <summary>One thing somebody is owed and was not online to receive.</summary>
    public class QueuedPayout
    {
        public string Uid { get; set; }
        public List<RewardItem> Items { get; set; }
        public long QueuedUnix { get; set; }

        /// <summary>What this is: "prize", "refund", "reward", "participation". Shown to nobody;
        /// it is there to make a stuck payout readable in the file and in the log.</summary>
        public string Reason { get; set; }

        /// <summary>Where it came from - an event code or a tournament id. Diagnostics only.</summary>
        public string Source { get; set; }

    }

    /// <summary>
    /// Items owed to players who were offline when the payout happened, handed over on their next
    /// login (ModConfig/canevents/payouts.json).
    ///
    /// Rewards are dealt out at the very end of a match, which is exactly when a losing player is
    /// most likely to have already closed the game - and a reward that needs its owner present is a
    /// reward that quietly evaporates.
    ///
    /// Tournaments keep their own queue (TournamentStore.Pending) rather than using this one, and
    /// that is deliberate: their debts are written in the SAME transaction that empties the prize
    /// pot, so a crash can never leave a pot that is gone and a debt that was never recorded.
    /// </summary>
    public class PayoutQueue : JsonFileStore<List<QueuedPayout>>
    {
        // The base keeps only the logger, and delivery needs the world to resolve item codes.
        private readonly ICoreServerAPI sapi;

        public PayoutQueue(ICoreServerAPI sapi) : base(sapi, "payouts.json", "PayoutQueue")
        {
            this.sapi = sapi;
        }

        public void Queue(string uid, List<RewardItem> items, string reason, string source = null)
        {
            if (string.IsNullOrEmpty(uid) || items == null || items.Count == 0) return;
            // A bot has no inventory, and a row for one would sit in the file for good.
            if (Bots.BotIdentity.IsBot(uid)) return;

            Queue(new QueuedPayout
            {
                Uid = uid,
                Items = CloneItems(items),
                QueuedUnix = UnixTime.Now,
                Reason = reason,
                Source = source
            });
        }

        public void Queue(QueuedPayout payout)
        {
            if (payout == null || payout.Items == null || payout.Items.Count == 0) return;
            lock (lockObj)
            {
                Data.Add(payout);
                Save();
            }
        }

        /// <summary>Takes (and removes) everything owed to this player. Removed before delivery on
        /// purpose - a second login must not pay twice - so a failed delivery is re-queued instead.</summary>
        public List<QueuedPayout> Take(string uid)
        {
            var mine = new List<QueuedPayout>();
            if (string.IsNullOrEmpty(uid)) return mine;

            lock (lockObj)
            {
                for (int i = Data.Count - 1; i >= 0; i--)
                {
                    if (Data[i].Uid != uid) continue;
                    mine.Add(Data[i]);
                    Data.RemoveAt(i);
                }
                if (mine.Count > 0) Save();
            }
            return mine;
        }

        /// <summary>
        /// Hands over everything owed. Each row is tried on its own and put back if it throws, so one
        /// unresolvable item code cannot swallow the rest of what somebody is owed.
        /// </summary>
        public int Deliver(IServerPlayer player, string context = "payout")
        {
            if (player == null) return 0;

            var owed = Take(player.PlayerUID);
            int delivered = 0;

            foreach (var p in owed)
            {
                try
                {
                    foreach (var item in p.Items)
                        EventManagers.InventoryUtil.GiveByCode(sapi, player, item.Code, item.Quantity, context);
                    delivered++;
                }
                catch (Exception e)
                {
                    sapi.World.Logger.Error("[canevents] Payout for {0} ({1}) failed, re-queued: {2}",
                        player.PlayerName, p.Reason, e.Message);
                    Queue(p);
                }
            }
            return delivered;
        }

        private static List<RewardItem> CloneItems(List<RewardItem> items)
        {
            var copy = new List<RewardItem>(items.Count);
            foreach (var it in items)
                if (it != null) copy.Add(new RewardItem { Code = it.Code, Quantity = it.Quantity });
            return copy;
        }
    }
}
