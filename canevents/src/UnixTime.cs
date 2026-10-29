using System;

namespace canevents.src
{
    public static class UnixTime
    {
        // Test override; null = real clock.
        public static Func<long> Source { get; set; }

        public static long Now => Source?.Invoke() ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        public static long NowMs => Source != null
            ? Source.Invoke() * 1000
            : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
}
