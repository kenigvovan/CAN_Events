using System.Collections.Generic;

namespace canevents.src.Scoring
{
    // Rake is burned, the rest split evenly, remainder goes to the first winner.
    public static class EntryFeePot
    {
        // Also used by the tournament pot.
        public static int AfterRake(int quantity, int rakePercent)
        {
            int rake = rakePercent < 0 ? 0 : (rakePercent > 100 ? 100 : rakePercent);
            // long: quantity * 100 can overflow int.
            return (int)((long)quantity * (100 - rake) / 100);
        }

        public static void AddToPot(Dictionary<string, int> pot, IEnumerable<EventManagers.events.RewardItem> items)
        {
            if (pot == null || items == null) return;
            foreach (var item in items)
                if (item != null && !string.IsNullOrEmpty(item.Code) && item.Quantity > 0)
                    pot[item.Code] = (pot.TryGetValue(item.Code, out var q) ? q : 0) + item.Quantity;
        }

        // code -> share per winner index. Codes that round to 0 are omitted.
        public static Dictionary<string, int[]> Split(
            IReadOnlyDictionary<string, int> pot, int rakePercent, int winnerCount)
        {
            var result = new Dictionary<string, int[]>();
            if (pot == null || winnerCount <= 0) return result;

            foreach (var kv in pot)
            {
                int payout = AfterRake(kv.Value, rakePercent);
                if (payout <= 0) continue;

                int per = payout / winnerCount;
                int remainder = payout % winnerCount;

                var shares = new int[winnerCount];
                for (int i = 0; i < winnerCount; i++)
                    shares[i] = per + (i == 0 ? remainder : 0);

                result[kv.Key] = shares;
            }
            return result;
        }
    }
}
