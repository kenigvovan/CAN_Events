using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;

namespace canevents.src.EventManagers.events
{
    // Shared normalization for an item list (entry fee or a reward set).
    public static class ItemSetUtil
    {
        /// <summary>
        /// Normalizes an item list into (code, qty) pairs, SUMMING duplicate codes and dropping
        /// blank/non-positive entries. The can-pay check and the charge must both run off this, or a
        /// fee listing 4+4 of one code is validated per-entry against the full carried count and mints
        /// the shortfall at payout.
        /// </summary>
        public static List<(string code, int qty)> Coalesce(IReadOnlyList<RewardItem> items)
        {
            var byCode = new Dictionary<string, int>();
            var order = new List<string>();
            if (items != null)
            {
                foreach (var it in items)
                {
                    if (it == null || it.Quantity <= 0 || string.IsNullOrEmpty(it.Code)) continue;
                    if (byCode.TryGetValue(it.Code, out var q)) byCode[it.Code] = q + it.Quantity;
                    else { byCode[it.Code] = it.Quantity; order.Add(it.Code); }
                }
            }
            var result = new List<(string, int)>(order.Count);
            foreach (var code in order) result.Add((code, byCode[code]));
            return result;
        }

        /// <summary>Packs an item list into the wire string the client expects ("code:qty;code:qty").
        /// Coalesced first so duplicate codes show as one summed icon. Null for an empty list.</summary>
        public static string Pack(IReadOnlyList<RewardItem> items)
        {
            var pairs = Coalesce(items);
            if (pairs.Count == 0) return null;
            var sb = new StringBuilder();
            for (int i = 0; i < pairs.Count; i++)
            {
                if (i > 0) sb.Append(';');
                sb.Append(pairs[i].code).Append(':').Append(pairs[i].qty);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Inverse of <see cref="Pack"/>. A missing or non-integer qty defaults to 1; blank entries are
        /// skipped. The item code itself may contain ':' (domain:path), so the qty is split on the LAST
        /// colon.
        /// </summary>
        public static List<RewardItem> Parse(string packed)
        {
            var result = new List<RewardItem>();
            if (string.IsNullOrEmpty(packed)) return result;
            foreach (var entry in packed.Split(';'))
            {
                if (string.IsNullOrEmpty(entry)) continue;
                string code = entry;
                int qty = 1;
                int lastColon = entry.LastIndexOf(':');
                if (lastColon > 0 && int.TryParse(entry.Substring(lastColon + 1), out int q))
                {
                    code = entry.Substring(0, lastColon);
                    qty = q;
                }
                if (!string.IsNullOrEmpty(code) && qty > 0)
                    result.Add(new RewardItem { Code = code, Quantity = qty });
            }
            return result;
        }
    }

    // Optional per-event entry fee / wager pool, configured under an "entryFee" object in the event
    // JSON. Every participant pays Items at QUEUE time (not match start) so the stake can't be dropped
    // during the countdown. The pot goes to the winner(s) at match end after RakePercent is burned:
    //   0    → winner-takes-all, the pot transfers intact
    //   1-99 → winner gets the remainder, the rake is destroyed (partial sink)
    //   100  → the whole pot is destroyed (the fee is purely an entry barrier)
    // A void result (too-short match, or a draw) refunds every stake, so the pot can't be grabbed by an
    // instant throw. The rake is the anti-collusion lever: a rigged match is a lossy laundering channel.
    public class EntryFeeSpec
    {
        // Item set each participant must pay to enter. Null/empty disables the fee for the event.
        [JsonProperty] public List<RewardItem> Items { get; set; }

        // Percentage of the collected pot destroyed before payout (0..100). Clamped on use.
        [JsonProperty] public int RakePercent { get; set; } = 0;
    }
}
