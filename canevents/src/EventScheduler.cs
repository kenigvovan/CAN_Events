using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers;
using canevents.src.EventManagers.events;
using canevents.src.EventPlacesManagers.Places;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace canevents.src
{
    public class EventScheduler
    {
        private ICoreServerAPI sapi;
        private Config config;
        private CANEventManager eventManager;
        private int rotationIndex = 0;
        private float secondsSinceLastEvent = 0f;
        private long tickListenerId;

        public bool Enabled { get; set; }

        // Set by SignupWindows; null falls back to a plain chat line.
        // Fully qualified: Vintagestory.API.Common has its own Func<>.
        public System.Func<string, bool> AnnounceHandler { get; set; }

        // For the admin panel
        public int IntervalMinutes => config.scheduleIntervalMinutes;
        public int MinPlayersToStart => config.minPlayersToStart;
        public int MaxConcurrentEvents => config.maxConcurrentEvents;
        public string[] RotationCodes => config.enabledGameModes ?? Array.Empty<string>();

        // -1 when disabled.
        public float SecondsUntilNext => Enabled
            ? Math.Max(0f, config.scheduleIntervalMinutes * 60f - secondsSinceLastEvent)
            : -1f;

        public string PeekNextEnabledCode()
        {
            var modes = config.enabledGameModes;
            if (modes == null || modes.Length == 0) return null;
            for (int tried = 0; tried < modes.Length; tried++)
            {
                string code = modes[(rotationIndex + tried) % modes.Length];
                if (!eventManager.IsEventEnabled(code)) continue;
                if (eventManager.FindEventByCode(code) != null) return code;
            }
            return null;
        }

        public EventScheduler(ICoreServerAPI sapi, Config config, CANEventManager eventManager)
        {
            this.sapi = sapi;
            this.config = config;
            this.eventManager = eventManager;
            this.Enabled = config.autoScheduleEnabled;

            tickListenerId = sapi.Event.RegisterGameTickListener(OnTick, 30000);

            sapi.World.Logger.Notification("EventScheduler initialized. Enabled={0}, Interval={1}min",
                Enabled, config.scheduleIntervalMinutes);
        }

        private void OnTick(float dt)
        {
            if (!Enabled) return;

            secondsSinceLastEvent += dt;

            float intervalSeconds = config.scheduleIntervalMinutes * 60f;
            if (secondsSinceLastEvent < intervalSeconds) return;

            if (eventManager.ScheduledMatchCount >= config.maxConcurrentEvents) return;

            int onlinePlayers = sapi.World.AllOnlinePlayers.Length;
            if (onlinePlayers < config.minPlayersToStart)
            {
                return;
            }

            if (config.enabledGameModes == null || config.enabledGameModes.Length == 0) return;

            // One sweep of the rotation, skipping disabled/unknown modes and ones with no free arena.
            CANEvent targetEvent = null;
            string nextCode = null;
            for (int tried = 0; tried < config.enabledGameModes.Length; tried++)
            {
                nextCode = config.enabledGameModes[rotationIndex % config.enabledGameModes.Length];
                rotationIndex++;
                if (!eventManager.IsEventEnabled(nextCode)) continue;

                var candidate = eventManager.FindEventByCode(nextCode);
                if (candidate == null)
                {
                    sapi.World.Logger.Warning("EventScheduler: Event '{0}' not found, skipping", nextCode);
                    continue;
                }

                var searchTypes = candidate.EventTypes.ToArray();
                if (!eventManager.eventPlacesManager.EventPlaces.Any(p => p.IsAvailableFor(searchTypes))) continue;

                targetEvent = candidate;
                break;
            }

            if (targetEvent == null) return;

            bool handled;
            if (AnnounceHandler != null)
            {
                // False = declined (e.g. window already open); announce nothing then.
                handled = AnnounceHandler(nextCode);
            }
            else
            {
                EventManagers.eventBehavior.Shared.EventBroadcaster.BroadcastToServer(
                    sapi, Lang.Get("canevents:event-announced", nextCode));
                handled = true;
            }

            // Reset even if declined, or it retries every tick and spins the rotation.
            secondsSinceLastEvent = 0f;
            sapi.World.Logger.Notification("EventScheduler: {0} event '{1}'",
                handled ? "Announced" : "Skipped (already announced)", nextCode);
        }

        // Starts with whoever is queued, ignoring the mode's thresholds.
        public bool ForceStart(string eventCode, out string reason)
        {
            var targetEvent = eventManager.FindEventByCode(eventCode);
            if (targetEvent == null)
            {
                reason = Lang.Get("canevents:event-not-found", eventCode);
                return false;
            }

            if (!eventManager.ForceComposeEvent(targetEvent, out var why))
            {
                reason = Lang.Get("canevents:force-start-failed", eventCode, why);
                return false;
            }

            // Arena is picked on the next location-search tick.
            EventManagers.eventBehavior.Shared.EventBroadcaster.BroadcastToServer(
                sapi, Lang.Get("canevents:event-admin-announced", eventCode));

            secondsSinceLastEvent = 0f;
            sapi.World.Logger.Notification("EventScheduler: Force-started event '{0}'", eventCode);
            reason = null;
            return true;
        }

        public void SetEnabled(bool enabled)
        {
            Enabled = enabled;
            config.autoScheduleEnabled = enabled;
            PersistConfig();
            sapi.World.Logger.Notification("EventScheduler: {0}", enabled ? "Enabled" : "Disabled");
        }

        public void SetInterval(int minutes)
        {
            // 0 would announce every tick; the chat command doesn't clamp.
            minutes = Math.Clamp(minutes, 1, 24 * 60);

            config.scheduleIntervalMinutes = minutes;
            secondsSinceLastEvent = 0f;
            PersistConfig();
            sapi.World.Logger.Notification("EventScheduler: Interval set to {0} minutes", minutes);
        }

        // Returns whether the rotation changed.
        public bool SetInRotation(string code, bool inRotation)
        {
            if (string.IsNullOrWhiteSpace(code)) return false;
            var list = new List<string>(config.enabledGameModes ?? Array.Empty<string>());
            int at = list.FindIndex(c => string.Equals(c, code, StringComparison.OrdinalIgnoreCase));

            // The tick only increments the index, normalize before adjusting.
            int oldCount = list.Count;
            rotationIndex = oldCount > 0 ? rotationIndex % oldCount : 0;

            if (inRotation)
            {
                if (at >= 0) return false;
                list.Add(code);
            }
            else
            {
                if (at < 0) return false;
                list.RemoveAt(at);
                // Keep pointing at the entry that was next.
                if (at < rotationIndex) rotationIndex--;
                if (rotationIndex >= list.Count) rotationIndex = 0;
            }

            config.enabledGameModes = list.ToArray();
            PersistConfig();
            sapi.World.Logger.Notification("EventScheduler: rotation is now [{0}]", string.Join(", ", list));
            return true;
        }

        private void PersistConfig() => AdminStateStore.SaveConfig(sapi, config);

        public void Dispose()
        {
            sapi.Event.UnregisterGameTickListener(tickListenerId);
        }
    }
}
