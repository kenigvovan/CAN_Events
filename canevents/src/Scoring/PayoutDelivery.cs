using System;
using System.Collections.Generic;
using canevents.src.EventManagers;
using canevents.src.EventManagers.events;
using Vintagestory.API.Server;

namespace canevents.src.Scoring
{
    // Shared by PayoutQueue and the tournament pending queue.
    public static class PayoutDelivery
    {
        // On throw, given = items already handed out, so only the rest gets re-queued.
        public static bool TryGiveAll(ICoreServerAPI sapi, IServerPlayer player, List<RewardItem> items,
            string context, out int given, out Exception error)
        {
            given = 0;
            error = null;
            if (items == null) return true;
            try
            {
                for (; given < items.Count; given++)
                {
                    var item = items[given];
                    if (item == null) continue;
                    InventoryUtil.GiveByCode(sapi, player, item.Code, item.Quantity, context);
                }
                return true;
            }
            catch (Exception e)
            {
                error = e;
                return false;
            }
        }

        public static List<RewardItem> Remainder(List<RewardItem> items, int given)
            => Clone(items.GetRange(given, items.Count - given));

        // Deep copy so queued rows don't share objects with config or the pot.
        public static List<RewardItem> Clone(IEnumerable<RewardItem> items)
        {
            var copy = new List<RewardItem>();
            if (items == null) return copy;
            foreach (var it in items)
                if (it != null) copy.Add(new RewardItem { Code = it.Code, Quantity = it.Quantity });
            return copy;
        }
    }
}
