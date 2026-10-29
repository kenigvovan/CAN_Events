using System;
using canevents.src.EventManagers;
using Vintagestory.API.Config;

namespace canevents.src.EventManagers.eventBehavior.Shared
{
    // Pre-match countdown: swallows ticks, announces each second, fires a callback at zero.
    public sealed class PrefightTimer
    {
        public const string HoldHudKey = "canevents:prefight-hold-hud";

        public static float ReadSeconds(events.BehaviorAttributes config, float defaultSeconds = 3f)
            => config.Float(BehaviorConfigKeys.PrefightSeconds, defaultSeconds);

        private float remaining;
        private RunningEvent holdOn;
        private RunningEvent announceTo;

        public bool Active { get; private set; }

        // With a match, also shows the countdown on the HUD (chat alone got missed).
        public void Start(float seconds, RunningEvent match = null, string holdLangKey = HoldHudKey)
        {
            remaining = seconds;
            Active = seconds > 0f;
            holdOn = Active ? match : null;
            announceTo = Active ? match : null;
            holdOn?.BeginHold(seconds, holdLangKey);
            if (announceTo != null)
                EventBroadcaster.Broadcast(announceTo,
                    Lang.Get("canevents:countdown-get-ready", (int)Math.Ceiling(seconds)));
        }

        // True = tick consumed, caller should return.
        public bool Tick(float dt, Action onElapsed)
        {
            if (!Active) return false;

            float previous = remaining;
            remaining -= dt;

            // Compare ceilings so each second fires once at any tick rate.
            int previousSecond = (int)Math.Ceiling(previous);
            int currentSecond = (int)Math.Ceiling(remaining);
            if (currentSecond != previousSecond && currentSecond > 0 && announceTo != null)
                EventBroadcaster.Broadcast(announceTo, Lang.Get("canevents:countdown-tick", currentSecond));

            if (remaining <= 0f)
            {
                Active = false;
                holdOn?.EndHold();
                holdOn = null;
                announceTo = null;
                onElapsed?.Invoke();
            }
            return true;
        }
    }
}
