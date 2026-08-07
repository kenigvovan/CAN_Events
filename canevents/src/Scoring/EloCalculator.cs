using System;

namespace canevents.src.Scoring
{
    // Pure Elo math. A "side" is one Elo entity (solo player or whole team); its rating is the mean of
    // its members' and every member shares the side's delta (team-average model). M-side matches use
    // pairwise rank decomposition: a virtual 1v1 per pair, with K scaled by 1/(M-1) so a side's total
    // swing stays comparable to a single 1v1. M=2 reduces to textbook Elo.
    public static class EloCalculator
    {
        public const int DefaultRating = 1000;
        public const int K = 32;

        /// <summary>
        /// Rating delta per side in an M-side match: sideRatings[i] are side i's member ratings,
        /// sideRanks[i] its placement (lower = better, equal = tie). Apply each delta to every member.
        /// </summary>
        public static int[] ComputeRanked(int[][] sideRatings, int[] sideRanks)
        {
            int m = sideRatings?.Length ?? 0;
            var deltas = new int[m];
            if (m < 2 || sideRanks == null || sideRanks.Length != m) return deltas;

            var avg = new double[m];
            for (int i = 0; i < m; i++) avg[i] = Mean(sideRatings[i]);

            double kPer = (double)K / (m - 1);
            for (int i = 0; i < m; i++)
            {
                double acc = 0.0;
                for (int j = 0; j < m; j++)
                {
                    if (j == i) continue;
                    double s = sideRanks[i] < sideRanks[j] ? 1.0
                             : sideRanks[i] > sideRanks[j] ? 0.0
                             : 0.5;
                    double e = 1.0 / (1.0 + Math.Pow(10.0, (avg[j] - avg[i]) / 400.0));
                    acc += s - e;
                }
                deltas[i] = (int)Math.Round(kPer * acc);
            }
            return deltas;
        }

        /// <summary>
        /// 2-team convenience over <see cref="ComputeRanked"/>. scoreA: 1.0 = A won, 0.0 = A lost, 0.5 = draw.
        /// </summary>
        public static (int deltaA, int deltaB) Compute(int[] teamARatings, int[] teamBRatings, double scoreA)
        {
            int[] ranks = scoreA > 0.5 ? new[] { 0, 1 }
                        : scoreA < 0.5 ? new[] { 1, 0 }
                        : new[] { 0, 0 };
            var d = ComputeRanked(new[] { teamARatings, teamBRatings }, ranks);
            return (d[0], d[1]);
        }

        public static double Mean(int[] ratings)
        {
            if (ratings == null || ratings.Length == 0) return DefaultRating;
            long sum = 0;
            foreach (var r in ratings) sum += r;
            return (double)sum / ratings.Length;
        }
    }
}
