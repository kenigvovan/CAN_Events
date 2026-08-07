using System.Collections.Generic;
using canevents.src.EventManagers.events;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace canevents.src.EventManagers
{
    // Persistent per-player escrow for an entry-fee wager, held in ModData (survives disconnect and
    // crash) together with the event code it was paid for. Charging at QUEUE time rather than match
    // start closes the "drop the fee during the countdown" dodge. Refunded if the player leaves the
    // queue, settled at match end by RewardDistributor, or refunded on reconnect when a crash left it
    // owning no queue slot and no running event.
    //
    // One ModData slot → one pending wager per player; the stored event code tells the refund logic
    // which commitment the stake belongs to.
    public static class EntryFeeEscrow
    {
        public const string ModDataKey = "canevents_entryfee_escrow";

        /// <summary>
        /// Charges the full fee from the player's inventory into escrow. Returns false without charging
        /// if they can't pay the full amount or already hold an escrow (one wager at a time).
        /// </summary>
        public static bool Charge(IServerPlayer player, string eventCode, IReadOnlyList<RewardItem> fee)
        {
            if (player == null || fee == null || fee.Count == 0) return false;
            if (player.GetModdata(ModDataKey) != null) return false;

            // Coalesce duplicate codes so [stone:4, stone:4] is charged as stone:8 rather than validated
            // twice against the full carried count, which would mint the shortfall.
            var pairs = ItemSetUtil.Coalesce(fee);
            if (pairs.Count == 0) return false;

            if (!InventoryUtil.Has(player, pairs)) return false; // can't pay the full ante

            var tree = new TreeAttribute();
            tree.SetString("eventCode", eventCode ?? "");
            tree.SetInt("count", pairs.Count);
            for (int i = 0; i < pairs.Count; i++)
            {
                // Has() above guarantees the full amount is there.
                InventoryUtil.Remove(player, pairs[i].code, pairs[i].qty);
                tree.SetString($"code_{i}", pairs[i].code);
                tree.SetInt($"qty_{i}", pairs[i].qty);
            }
            player.SetModdata(ModDataKey, tree.ToBytes());
            return true;
        }

        // Loads the escrow ModData as a tree, or null when nothing is stored / it can't be parsed.
        private static TreeAttribute ReadTree(IServerPlayer player)
        {
            byte[] data = player?.GetModdata(ModDataKey);
            if (data == null || data.Length == 0) return null;
            try { var tree = new TreeAttribute(); tree.FromBytes(data); return tree; }
            catch { return null; }
        }

        /// <summary>The event code the held escrow was paid for, or null when the player holds none.</summary>
        public static string EventCodeOf(IServerPlayer player)
        {
            var code = ReadTree(player)?.GetString("eventCode");
            return string.IsNullOrEmpty(code) ? null : code;
        }

        /// <summary>The escrowed items, or null when the player holds no escrow.</summary>
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

        /// <summary>True if the player currently holds an entry-fee escrow.</summary>
        public static bool Has(IServerPlayer player) => player?.GetModdata(ModDataKey) != null;

        /// <summary>Drops the escrow record without returning anything (the stake was paid out / sunk).</summary>
        public static void Clear(IServerPlayer player) => player?.RemoveModdata(ModDataKey);
    }
}
