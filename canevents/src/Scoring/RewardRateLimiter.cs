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

    // Per-player token bucket limiting WINNER rewards over time: one token per rewarded win, refilling
    // continuously up to <burst>, so a player may burst and is then throttled to the refill rate. Caps
    // sustained farming regardless of how wins are spaced - harder to game than a sliding window.
    public class RewardRateLimiter : JsonFileStore<Dictionary<string, RewardBucket>>
    {
        // Fallback for a non-positive configured refill: a finite refill keeps the store self-bounding
        // (buckets refill to full and get pruned). "Never refill" would mean remembering every winner
        // forever, so it is not allowed.
        private const double FallbackRefillHours = 24.0;

        // Mutable so an admin config change applies live via Reconfigure; read/written under lockObj.
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
            Reconfigure(burst, refillHoursPerToken); // applies the fallback for a non-positive refill
        }

        /// <summary>
        /// Live update of burst/refill. Existing buckets keep their tokens; the new rate takes effect on
        /// the next refill. A non-positive refill falls back to <see cref="FallbackRefillHours"/>.
        /// </summary>
        public void Reconfigure(int burst, double refillHoursPerToken)
        {
            if (refillHoursPerToken <= 0) refillHoursPerToken = FallbackRefillHours;
            lock (lockObj)
            {
                capacity = Math.Max(1, burst);
                refillPerSecond = 1.0 / (refillHoursPerToken * 3600.0);
            }
        }

        // The CALLER decides scope: a non-empty scope (event code) gives that event its own bucket,
        // null/empty shares one per player. "|" can't occur in a player UID or an event code.
        private static string Key(string uid, string scope)
            => string.IsNullOrEmpty(scope) ? uid : uid + "|" + scope;

        /// <summary>
        /// Refills the bucket for elapsed time, then consumes a token if available; false means the
        /// bucket is empty and the reward should be denied. Call <see cref="Flush"/> after a batch.
        /// </summary>
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

        /// <summary>Available tokens for a (player, event) bucket right now (read-only; refilled to now).</summary>
        private double Peek(string uid, string eventCode, long nowUnix)
        {
            lock (lockObj)
            {
                if (!Data.TryGetValue(Key(uid, eventCode), out var b)) return capacity;
                return TokensOf(b, nowUnix);
            }
        }

        /// <summary>
        /// UI snapshot: whole winner rewards available now, and seconds until the next whole token
        /// refills (0 when full).
        /// </summary>
        public (int remaining, double secondsToNext) PeekStatus(string uid, string eventCode, long nowUnix)
        {
            double tokens = Peek(uid, eventCode, nowUnix);
            int remaining = (int)Math.Floor(tokens);
            double secondsToNext = 0;
            if (tokens < capacity)
                secondsToNext = (1.0 - (tokens - Math.Floor(tokens))) / refillPerSecond;
            return (remaining, secondsToNext);
        }

        /// <summary>Clears a player's throttle - admin override for a false positive. Drops their shared
        /// bucket and every per-event bucket they hold.</summary>
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

        /// <summary>Availability for the admin "ratecap show" command; anything unlisted is full.</summary>
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

        /// <summary>Persists pending changes (call once after a match's rewards are distributed).</summary>
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

        // Drops buckets refilled back to full: indistinguishable from a fresh player, so removing them
        // changes nothing while bounding the store to currently-throttled players. Caller holds lockObj.
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
