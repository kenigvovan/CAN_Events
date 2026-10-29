using System;
using System.Collections.Generic;
using Vintagestory.API.Server;

namespace canevents.src.Scoring
{
    public class RewardBucket
    {
        public double Tokens { get; set; }
        public long LastUnix { get; set; }
    }

    // Token bucket per player for winner rewards: one token per win, refills up to burst.
    public class RewardRateLimiter : JsonFileStore<Dictionary<string, RewardBucket>>
    {
        // Used when refill <= 0; without refill full buckets are never pruned.
        private const double FallbackRefillHours = 24.0;

        // Guarded by lockObj.
        private double capacity;
        private double refillPerSecond;
        private bool dirty;

        public RewardRateLimiter(ICoreServerAPI sapi, int burst, double refillHoursPerToken)
            : base(sapi, "rewardratecap.json", "RewardRateLimiter")
        {
            if (refillHoursPerToken <= 0)
                sapi.World.Logger.Warning(
                    "RewardRateLimiter: rewardRateCapRefillHours must be > 0 (got {0}); using {1}h so the store stays bounded.",
                    refillHoursPerToken, FallbackRefillHours);
            Reconfigure(burst, refillHoursPerToken);
        }

        // Existing buckets keep their tokens.
        public void Reconfigure(int burst, double refillHoursPerToken)
        {
            if (refillHoursPerToken <= 0) refillHoursPerToken = FallbackRefillHours;
            lock (lockObj)
            {
                capacity = Math.Max(1, burst);
                refillPerSecond = 1.0 / (refillHoursPerToken * 3600.0);
            }
        }

        // Empty scope = shared bucket. "|" can't appear in a uid or event code.
        private static string Key(string uid, string scope)
            => string.IsNullOrEmpty(scope) ? uid : uid + "|" + scope;

        // Call Flush after a batch.
        public bool TryConsume(string uid, string eventCode, long nowUnix)
        {
            if (string.IsNullOrEmpty(uid)) return true;
            string key = Key(uid, eventCode);
            lock (lockObj)
            {
                if (!Data.TryGetValue(key, out var b))
                    Data[key] = b = new RewardBucket { Tokens = capacity, LastUnix = nowUnix };

                b.Tokens = TokensOf(b, nowUnix);
                b.LastUnix = nowUnix;

                bool allowed = b.Tokens >= 1.0;
                if (allowed) b.Tokens -= 1.0;
                dirty = true;
                return allowed;
            }
        }

        private double Peek(string uid, string eventCode, long nowUnix)
        {
            lock (lockObj)
            {
                if (!Data.TryGetValue(Key(uid, eventCode), out var b)) return capacity;
                return TokensOf(b, nowUnix);
            }
        }

        // secondsToNext is 0 when full.
        public (int remaining, double secondsToNext) PeekStatus(string uid, string eventCode, long nowUnix)
        {
            double tokens = Peek(uid, eventCode, nowUnix);
            int remaining = (int)Math.Floor(tokens);
            double secondsToNext = 0;
            if (tokens < capacity)
                secondsToNext = (1.0 - (tokens - Math.Floor(tokens))) / refillPerSecond;
            return (remaining, secondsToNext);
        }

        // Drops the shared bucket and all per-event ones.
        public void Reset(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return;
            lock (lockObj)
            {
                string prefix = uid + "|";
                var keys = new List<string>();
                foreach (var k in Data.Keys)
                    if (k == uid || k.StartsWith(prefix, StringComparison.Ordinal)) keys.Add(k);
                if (keys.Count == 0) return;
                foreach (var k in keys) Data.Remove(k);
                Save();
                dirty = false;
            }
        }

        private double TokensOf(RewardBucket b, long nowUnix)
            => Math.Min(capacity, b.Tokens + Math.Max(0, nowUnix - b.LastUnix) * refillPerSecond);

        // For "ratecap show".
        public string DescribeAvailability(string uid, long nowUnix)
        {
            lock (lockObj)
            {
                string prefix = uid + "|";
                var parts = new List<string>();
                if (Data.TryGetValue(uid, out var shared))
                    parts.Add(string.Format("shared: {0:0.##}", TokensOf(shared, nowUnix)));
                foreach (var kv in Data)
                    if (kv.Key.StartsWith(prefix, StringComparison.Ordinal))
                        parts.Add(string.Format("{0}: {1:0.##}", kv.Key.Substring(prefix.Length), TokensOf(kv.Value, nowUnix)));

                return parts.Count == 0
                    ? string.Format("full ({0}) everywhere.", capacity)
                    : string.Join(", ", parts) + string.Format(" (of {0}; unlisted = full).", capacity);
            }
        }

        public void Flush()
        {
            lock (lockObj)
            {
                if (!dirty) return;
                PruneFull(NowUnix());
                Save();
                dirty = false;
            }
        }

        // A full bucket equals a fresh one, so drop it. Caller holds lockObj.
        private void PruneFull(long nowUnix)
        {
            List<string> stale = null;
            foreach (var kv in Data)
            {
                if (TokensOf(kv.Value, nowUnix) >= capacity) (stale ??= new List<string>()).Add(kv.Key);
            }
            if (stale != null)
                foreach (var uid in stale) Data.Remove(uid);
        }

        private static long NowUnix() => UnixTime.Now;
    }
}
