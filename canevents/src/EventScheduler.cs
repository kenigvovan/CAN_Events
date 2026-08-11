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

        /// <summary>
        /// What "announce this event" means. Returns true when it took the announcement over - the
        /// signup windows do, by opening one, which tells players in chat AND gives them a countdown
        /// and a way in. Left null (or returning false) the scheduler falls back to the bare chat
        /// line it always sent. A delegate rather than a reference: the windows already hold a
        /// reference to the scheduler, and two-way would be a cycle.
        /// </summary>
        // Namespace spelled out: Vintagestory.API.Common declares a Func<> of its own, and it is in
        // scope here alongside System.
        public System.Func<string, bool> AnnounceHandler { get; set; }

        // State exposed to the admin panel
        public int IntervalMinutes => config.scheduleIntervalMinutes;
        public int MinPlayersToStart => config.minPlayersToStart;
        public int MaxConcurrentEvents => config.maxConcurrentEvents;
        public string[] RotationCodes => config.enabledGameModes ?? Array.Empty<string>();

        /// <summary>Seconds until the next scheduling attempt, or -1 when disabled.</summary>
        public float SecondsUntilNext => Enabled
            ? Math.Max(0f, config.scheduleIntervalMinutes * 60f - secondsSinceLastEvent)
            : -1f;

        /// <summary>Which rotation entry would be tried next; leaves rotationIndex alone.</summary>
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

            if (eventManager.CurrentlyRunningEvents.Count >= config.maxConcurrentEvents) return;

            int onlinePlayers = sapi.World.AllOnlinePlayers.Length;
            if (onlinePlayers < config.minPlayersToStart)
            {
                return;
            }

            if (config.enabledGameModes == null || config.enabledGameModes.Length == 0) return;

            // Walk the rotation skipping codes that are disabled, unknown, or whose arenas are all
            // busy; testing availability inside the walk keeps the tick productive instead of
            // burning it on a busy mode. One full sweep max.
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

                // Same availability test the actual place lookup uses, see EventPlace.IsAvailableFor.
                var searchTypes = candidate.EventTypes.ToArray();
                if (!eventManager.eventPlacesManager.EventPlaces.Any(p => p.IsAvailableFor(searchTypes))) continue;

                targetEvent = candidate;
                break;
            }

            if (targetEvent == null) return;

            // Opening a signup window is the real announcement: it says the same thing in chat and
            // adds the countdown, the panel and the hold on early assembly. The bare line is only for
            // installs where nothing took the announcement over.
            bool handled;
            if (AnnounceHandler != null)
            {
                // False means it declined - a window for this event is already open, say. Then nothing
                // is announced at all; repeating the old chat line on top of an open window would be
                // the announcement saying one thing and the panel another.
                handled = AnnounceHandler(nextCode);
            }
            else
            {
                EventManagers.eventBehavior.Shared.EventBroadcaster.BroadcastToServer(
                    sapi, Lang.Get("canevents:event-announced", nextCode));
                handled = true;
            }

            // Reset either way: a declined turn waits for the next interval rather than retrying every
            // tick, which would also spin the rotation index past every other mode.
            secondsSinceLastEvent = 0f;
            sapi.World.Logger.Notification("EventScheduler: {0} event '{1}'",
                handled ? "Announced" : "Skipped (already announced)", nextCode);
        }

        /// <summary>
        /// Admin command: starts an event now from whoever is already queued, bypassing the mode's
        /// thresholds. Announcing alone would not do - it only invites registrations.
        /// </summary>
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

            // The match is already assembled; it picks an arena on the next location-search tick.
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
            // Clamped here rather than at each caller: the admin panel already did, the chat command
            // did not, and zero means the interval check never holds - an announcement every tick.
            minutes = Math.Clamp(minutes, 1, 24 * 60);

            config.scheduleIntervalMinutes = minutes;
            secondsSinceLastEvent = 0f;
            PersistConfig();
            sapi.World.Logger.Notification("EventScheduler: Interval set to {0} minutes", minutes);
        }

        // Admin toggles must persist, or they silently revert on restart.
        private void PersistConfig() => AdminStateStore.SaveConfig(sapi, config);

        public void Dispose()
        {
            sapi.Event.UnregisterGameTickListener(tickListenerId);
        }
    }
}
