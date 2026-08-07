using System.Collections.Generic;

namespace canevents.src.Scoring
{
    /// <summary>
    /// Splitting an entry-fee pot among winners: rake off the top, the rest divided evenly, and the
    /// integer-division remainder to the first winner rather than vanishing. Kept a pure function
    /// (apart from the rest of settlement) so the payout math is checkable without running a match.
    /// </summary>
    public static class EntryFeePot
    {
        /// <summary>
        /// Per-item payouts indexed by winner. Rake is clamped to 0..100 and burned (paid to nobody);
        /// item codes whose payout rounds down to zero are omitted.
        /// </summary>
        public static Dictionary<string, int[]> Split(
            IReadOnlyDictionary<string, int> pot, int rakePercent, int winnerCount)
        {
            var result = new Dictionary<string, int[]>();
            if (pot == null || winnerCount <= 0) return result;

            int rake = rakePercent < 0 ? 0 : (rakePercent > 100 ? 100 : rakePercent);

            foreach (var kv in pot)
            {
                // long intermediate: a large pot times 100 would overflow int before dividing.
                int payout = (int)((long)kv.Value * (100 - rake) / 100);
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
