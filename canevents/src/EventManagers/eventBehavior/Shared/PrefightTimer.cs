using System;

namespace canevents.src.EventManagers.eventBehavior.Shared
{
    /// <summary>
    /// The "get ready" countdown a mode runs before play starts: it swallows ticks while counting,
    /// announces each whole second, and fires a callback once when it expires.
    /// </summary>
    public sealed class PrefightTimer
    {
        private float remaining;

        /// <summary>True while the countdown is still running and the mode should not tick normally.</summary>
        public bool Active { get; private set; }

        /// <summary>Begins the countdown. A non-positive duration leaves it inactive.</summary>
        public void Start(float seconds)
        {
            remaining = seconds;
            Active = seconds > 0f;
        }

        /// <summary>
        /// Advances the countdown. Returns true when the tick was consumed, in which case the caller
        /// must return without running its normal per-tick logic. <paramref name="onSecond"/> gets
        /// each whole second (never zero); <paramref name="onElapsed"/> fires once at zero.
        /// </summary>
        public bool Tick(float dt, Action<int> onSecond, Action onElapsed)
        {
            if (!Active) return false;

            float previous = remaining;
            remaining -= dt;

            // Compare ceilings, not floats: makes "3... 2... 1..." fire exactly once per second
            // regardless of tick rate.
            int previousSecond = (int)Math.Ceiling(previous);
            int currentSecond = (int)Math.Ceiling(remaining);
            if (currentSecond != previousSecond && currentSecond > 0) onSecond?.Invoke(currentSecond);

            if (remaining <= 0f)
            {
                Active = false;
                onElapsed?.Invoke();
            }
            return true;
        }
    }
}
