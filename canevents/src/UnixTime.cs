using System;

namespace canevents.src
{
    /// <summary>
    /// The one clock the mod reads: every deadline is a unix second taken from here. A single seam
    /// also lets tests move time instead of waiting for it.
    /// </summary>
    public static class UnixTime
    {
        /// <summary>Overrides the clock, for tests. Null uses the real one.</summary>
        public static Func<long> Source { get; set; }

        public static long Now => Source?.Invoke() ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        /// <summary>Milliseconds, for the few places that measure shorter spans.</summary>
        public static long NowMs => Source != null
            ? Source.Invoke() * 1000
            : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
}
