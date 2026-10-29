using System.Collections.Generic;
using canevents.src.EventManagers.events;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace canevents.src.EventManagers
{
    // Entry fee held in ModData from queue time (survives crash). One wager per player at a time.
    public static class EntryFeeEscrow
    {
        public const string ModDataKey = "canevents_entryfee_escrow";

        // All or nothing; false if they can't pay or already hold an escrow.
        public static bool Charge(IServerPlayer player, string eventCode, IReadOnlyList<RewardItem> fee)
        {
            if (player == null || fee == null || fee.Count == 0) return false;
            if (Has(player)) return false;

            // Coalesce duplicates, or [stone:4, stone:4] would pass the check against 4 carried.
            var pairs = ItemSetUtil.Coalesce(fee);
            if (pairs.Count == 0) return false;

            if (!InventoryUtil.Has(player, pairs)) return false;

            var tree = new TreeAttribute();
            tree.SetString("eventCode", eventCode ?? "");
            tree.SetInt("count", pairs.Count);
            for (int i = 0; i < pairs.Count; i++)
            {
                InventoryUtil.Remove(player, pairs[i].code, pairs[i].qty);
                tree.SetString($"code_{i}", pairs[i].code);
                tree.SetInt($"qty_{i}", pairs[i].qty);
            }
            player.SetModdata(ModDataKey, tree.ToBytes());
            return true;
        }

        private static TreeAttribute ReadTree(IServerPlayer player)
        {
            byte[] data = player?.GetModdata(ModDataKey);
            if (data == null || data.Length == 0) return null;
            try { var tree = new TreeAttribute(); tree.FromBytes(data); return tree; }
            catch { return null; }
        }

        public static string EventCodeOf(IServerPlayer player)
        {
            var code = ReadTree(player)?.GetString("eventCode");
            return string.IsNullOrEmpty(code) ? null : code;
        }

        public static List<RewardItem> Read(IServerPlayer player)
        {
            var tree = ReadTree(player);
            if (tree == null) return null;
            int count = tree.GetInt("count", 0);
            var items = new List<RewardItem>(count);
            for (int i = 0; i < count; i++)
            {
                string code = tree.GetString($"code_{i}");
                int qty = tree.GetInt($"qty_{i}", 0);
                if (!string.IsNullOrEmpty(code) && qty > 0)
                    items.Add(new RewardItem { Code = code, Quantity = qty });
            }
            return items.Count > 0 ? items : null;
        }

        // Must match ReadTree's notion of empty, or a player gets stuck refused from paid queues.
        public static bool Has(IServerPlayer player)
        {
            byte[] data = player?.GetModdata(ModDataKey);
            return data != null && data.Length > 0;
        }

        // Doesn't refund.
        public static void Clear(IServerPlayer player) => player?.RemoveModdata(ModDataKey);
    }
}
