using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;

namespace canevents.src.EventManagers.events
{
    public static class ItemSetUtil
    {
        // Sums duplicate codes. Both the can-pay check and the charge must use this, or 4+4 of one code passes with only 4.
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

        // Wire format "code:qty;code:qty", null when empty.
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

        // Split on the last ':' since codes contain domain:path. Bad qty = 1.
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

    // "entryFee" in event JSON. Paid at queue time, not match start. Void result (draw, too short) refunds all.
    public class EntryFeeSpec
    {
        // Null/empty = no fee.
        [JsonProperty] public List<RewardItem> Items { get; set; }

        // % of the pot destroyed before payout; 100 = pure entry barrier.
        [JsonProperty] public int RakePercent { get; set; } = 0;
    }
}
