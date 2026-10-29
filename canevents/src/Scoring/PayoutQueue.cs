using System;
using System.Collections.Generic;
using canevents.src.EventManagers.events;
using Vintagestory.API.Server;

namespace canevents.src.Scoring
{
    public class QueuedPayout
    {
        public string Uid { get; set; }
        public List<RewardItem> Items { get; set; }
        public long QueuedUnix { get; set; }

        // "prize", "refund", "reward", "participation". Diagnostics only.
        public string Reason { get; set; }

        // Event code or tournament id. Diagnostics only.
        public string Source { get; set; }

    }

    // Items owed to offline players, given on next login (ModConfig/canevents/payouts.json).
    // Tournaments use TournamentStore.Pending instead, written in the same save as the pot.
    public class PayoutQueue : JsonFileStore<List<QueuedPayout>>
    {
        private readonly ICoreServerAPI sapi;

        public PayoutQueue(ICoreServerAPI sapi) : base(sapi, "payouts.json", "PayoutQueue")
        {
            this.sapi = sapi;
        }

        public void Queue(string uid, List<RewardItem> items, string reason, string source = null)
        {
            if (string.IsNullOrEmpty(uid) || items == null || items.Count == 0) return;
            // Bots never log in, the row would stay forever.
            if (Bots.BotIdentity.IsBot(uid)) return;

            Queue(new QueuedPayout
            {
                Uid = uid,
                Items = PayoutDelivery.Clone(items),
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

        // Removed before delivery so a second login can't pay twice; failures are re-queued.
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

        // Per row, so one bad item code doesn't block the rest.
        public int Deliver(IServerPlayer player, string context = "payout")
        {
            if (player == null) return 0;

            var owed = Take(player.PlayerUID);
            int delivered = 0;

            foreach (var p in owed)
            {
                if (p.Items == null) continue;
                if (PayoutDelivery.TryGiveAll(sapi, player, p.Items, context, out int given, out var e))
                {
                    delivered++;
                    continue;
                }

                sapi.World.Logger.Error("[canevents] Payout for {0} ({1}) failed after {2} of {3} item(s), "
                    + "rest re-queued: {4}", player.PlayerName, p.Reason, given, p.Items.Count, e.Message);
                Queue(new QueuedPayout
                {
                    Uid = p.Uid,
                    Items = PayoutDelivery.Remainder(p.Items, given),
                    QueuedUnix = p.QueuedUnix,
                    Reason = p.Reason,
                    Source = p.Source
                });
            }
            return delivered;
        }
    }
}
