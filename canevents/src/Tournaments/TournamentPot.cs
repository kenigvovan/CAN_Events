using System;
using System.Collections.Generic;

namespace canevents.src.Tournaments
{
    /// <summary>
    /// Pot arithmetic: rake off the top, then a split by placement percentages rather than the even
    /// winner-takes-it split of a single match (see EntryFeePot). Pure, so it is checkable alone.
    /// </summary>
    public static class TournamentPot
    {
        /// <summary>
        /// Per-place payouts, one code-to-quantity map per paid place. Percents are normalized (need
        /// not sum to 100) and truncated to the places that exist, so unused shares are redistributed
        /// rather than burned. Rake is burned; integer leftovers go to first place.
        /// </summary>
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
                // No usable configuration: everything to first place rather than nowhere.
                weights[0] = 1;
                totalWeight = 1;
            }

            for (int i = 0; i < places; i++) result.Add(new Dictionary<string, int>());

            int rake = rakePercent < 0 ? 0 : (rakePercent > 100 ? 100 : rakePercent);

            foreach (var kv in pot)
            {
                int payout = (int)((long)kv.Value * (100 - rake) / 100);
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

        /// <summary>
        /// Divides one place's share among a team's members; the remainder goes to the first member,
        /// as in EntryFeePot.
        /// </summary>
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
