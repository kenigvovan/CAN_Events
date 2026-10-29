using System;
using System.Collections.Generic;

namespace canevents.src.Tournaments
{
    // Rake, then split by placement percentages.
    public static class TournamentPot
    {
        // Percents are normalized over existing places. Rake is burned, rounding leftovers go to 1st.
        public static List<Dictionary<string, int>> Split(
            IReadOnlyDictionary<string, int> pot, int rakePercent, IReadOnlyList<int> percents, int places)
        {
            var result = new List<Dictionary<string, int>>();
            if (pot == null || places <= 0) return result;

            var weights = new List<int>();
            if (percents != null)
                for (int i = 0; i < percents.Count && i < places; i++)
                    weights.Add(Math.Max(0, percents[i]));
            while (weights.Count < places) weights.Add(0);

            long totalWeight = 0;
            foreach (int w in weights) totalWeight += w;
            if (totalWeight <= 0)
            {
                // Bad config: all to first place.
                weights[0] = 1;
                totalWeight = 1;
            }

            for (int i = 0; i < places; i++) result.Add(new Dictionary<string, int>());

            foreach (var kv in pot)
            {
                int payout = Scoring.EntryFeePot.AfterRake(kv.Value, rakePercent);
                if (payout <= 0) continue;

                int handed = 0;
                for (int i = 0; i < places; i++)
                {
                    int share = (int)((long)payout * weights[i] / totalWeight);
                    if (share > 0) result[i][kv.Key] = share;
                    handed += share;
                }

                int leftover = payout - handed;
                if (leftover > 0)
                    result[0][kv.Key] = (result[0].TryGetValue(kv.Key, out int had) ? had : 0) + leftover;
            }

            return result;
        }

        // Remainder goes to the first member, like EntryFeePot.
        public static List<Dictionary<string, int>> SplitAmongMembers(
            IReadOnlyDictionary<string, int> share, int members)
        {
            var result = new List<Dictionary<string, int>>();
            if (members <= 0) return result;
            for (int i = 0; i < members; i++) result.Add(new Dictionary<string, int>());
            if (share == null) return result;

            foreach (var kv in share)
            {
                int per = kv.Value / members;
                int remainder = kv.Value % members;
                for (int i = 0; i < members; i++)
                {
                    int give = per + (i == 0 ? remainder : 0);
                    if (give > 0) result[i][kv.Key] = give;
                }
            }
            return result;
        }
    }
}
