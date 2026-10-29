using System;

namespace canevents.src.Scoring
{
    // Side rating = team mean, all members get the side's delta.
    // M sides: pairwise virtual 1v1s with K/(M-1).
    public static class EloCalculator
    {
        public const int DefaultRating = 1000;
        public const int K = 32;

        // Ranks: lower is better, equal = tie.
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

        // scoreA: 1 = A won, 0 = A lost, 0.5 = draw.
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
