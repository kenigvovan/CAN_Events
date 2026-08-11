using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers.eventBehavior.Shared;
using canevents.src.EventManagers.events;
using canevents.src.Network;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace canevents.src.EventManagers
{
    /// <summary>
    /// One open registration window: an event whose queue is being filled on purpose, with the
    /// automatic assembly held back until the window closes.
    /// </summary>
    public class SignupWindow
    {
        public string EventCode;
        /// <summary>When the window closes by itself. Null for a window the admin closes by hand.</summary>
        public DateTime? EndsUtc;
        /// <summary>Last sign-up count sent to the invite panels; -1 until the first tick.</summary>
        public int LastRegistered = -1;

        /// <summary>
        /// Opened by the auto-scheduler rather than by a person. Such a window comes round every
        /// interval whether anyone is there or not, so it dies quietly: an empty server would
        /// otherwise get "nobody signed up" every half hour.
        /// </summary>
        public bool Auto;

        /// <summary>
        /// The lobby filled up, so the queue manager is allowed to assemble - but the window is still
        /// open. Letting it go on holding would keep the match from ever forming; closing it outright
        /// would lose the window if that assembly then fails (everyone queued busy in another match).
        /// </summary>
        public bool HoldReleased;

        public float SecondsLeft => EndsUtc == null
            ? -1f
            : Math.Max(0f, (float)(EndsUtc.Value - DateTime.UtcNow).TotalSeconds);
    }

    /// <summary>
    /// Registration windows: "the event opens now, you have five minutes to join". The mod's queue
    /// managers start a match as soon as their minimum is met, which leaves everyone who was not
    /// watching chat behind - a window holds that back, invites every player, and starts only when the
    /// time is up, when the lobby is full, or when the admin says so.
    ///
    /// The invitation is said once: one chat line carrying the command, one packet raising the HUD
    /// panel that carries the countdown. Nothing is sent on a timer afterwards - state goes out when
    /// it changes, and the clock is counted down by the client from the last packet it got.
    /// </summary>
    public class SignupWindows
    {
        public const int MaxWindowMinutes = 24 * 60;

        private readonly ICoreServerAPI sapi;
        private readonly CANEventManager manager;
        private readonly EventScheduler scheduler;
        private readonly object _lock = new object();

        private readonly Dictionary<string, SignupWindow> windows =
            new Dictionary<string, SignupWindow>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The one-second tick, or 0 while no window is open. Guarded by <see cref="_lock"/>.</summary>
        private long tickListenerId;
        private readonly PlayerDelegate _onPlayerNowPlaying;
        private readonly Action<RunningEvent, MatchStartSource> _onMatchStarted;

        public SignupWindows(ICoreServerAPI sapi, CANEventManager manager, EventScheduler scheduler)
        {
            this.sapi = sapi;
            this.manager = manager;
            this.scheduler = scheduler;

            // A player who logs in mid-window is invited too, or joining late means noticing the chat
            // line that scrolled past while they were loading.
            _onPlayerNowPlaying = OnPlayerNowPlaying;
            sapi.Event.PlayerNowPlaying += _onPlayerNowPlaying;

            // A window's job ends when the match it was filling actually starts - by any route: the
            // deadline, a full lobby assembled by the queue manager, or an admin force-start. Hearing
            // it from the match driver means no start path can forget to say so.
            //
            // External starts are not that: a tournament running a bracket match of the same event
            // code takes nobody out of the queue, and closing the window on it would end a sign-up
            // that was still collecting players.
            _onMatchStarted = (re, source) =>
            {
                if (source == MatchStartSource.External) return;
                Close(re?.Event?.Code, "started");
            };
            manager.OnMatchStarted += _onMatchStarted;
        }

        // The tick exists only while there is something to tick. Windows are open for minutes a day;
        // a listener running every second for the rest of the session is work nobody asked for.
        // Both helpers are called with _lock held.
        private void EnsureTicking()
        {
            // One second: the deadline and the "lobby is full" check both want to be acted on promptly.
            if (tickListenerId == 0) tickListenerId = sapi.Event.RegisterGameTickListener(OnTick, 1000);
        }

        private void StopTickingIfIdle()
        {
            if (windows.Count > 0 || tickListenerId == 0) return;
            sapi.Event.UnregisterGameTickListener(tickListenerId);
            tickListenerId = 0;
        }

        private ServerNetworkHandler Net => manager.NetworkHandler;

        // Queries

        /// <summary>True while this event's automatic assembly is held back by an open window.</summary>
        public bool IsHolding(string eventCode)
        {
            if (string.IsNullOrEmpty(eventCode)) return false;
            lock (_lock)
                return windows.TryGetValue(eventCode, out var window) && !window.HoldReleased;
        }

        /// <summary>Every open window as (event code, seconds left), for the admin dashboard.</summary>
        public List<(string code, float secondsLeft)> OpenWindows()
        {
            lock (_lock)
                return windows.Values.Select(w => (w.EventCode, w.SecondsLeft)).ToList();
        }

        // Admin operations

        /// <summary>
        /// Opens registration for an event. <paramref name="minutes"/> 0 means no deadline: the window
        /// stays open until /eventadmin start or /eventadmin cancel. <paramref name="auto"/> marks a
        /// window the scheduler opened on its own - see <see cref="SignupWindow.Auto"/>.
        /// </summary>
        public bool Open(string eventCode, int minutes, out string message, bool auto = false)
        {
            var ev = manager.FindEventByCode(eventCode);
            if (ev == null)
            {
                message = Lang.Get("canevents:event-not-found", eventCode);
                return false;
            }
            if (!manager.IsEventEnabled(ev.Code))
            {
                message = Lang.Get("canevents:queue-disabled", ev.Code);
                return false;
            }
            // Refused here rather than at the deadline: without a queue manager the sign-ups would be
            // collected for minutes and then fail to start, with everyone still sitting in the queue.
            if (manager.QueueManagers == null || !manager.QueueManagers.ContainsKey(ev.Code))
            {
                message = Lang.Get("canevents:signup-no-queue-manager", ev.Code);
                return false;
            }

            // Zero is a real setting - a window only the admin closes - so a negative number cannot be
            // clamped into it silently; a mistyped "-5" would look like it worked and never start.
            if (minutes < 0)
            {
                message = Lang.Get("canevents:signup-bad-minutes", MaxWindowMinutes);
                return false;
            }
            minutes = Math.Min(minutes, MaxWindowMinutes);

            SignupWindow window;
            lock (_lock)
            {
                if (windows.ContainsKey(ev.Code))
                {
                    message = Lang.Get("canevents:signup-already-open", ev.Code);
                    return false;
                }
                window = new SignupWindow
                {
                    EventCode = ev.Code,
                    EndsUtc = minutes > 0 ? DateTime.UtcNow.AddMinutes(minutes) : (DateTime?)null,
                    Auto = auto
                };
                windows[ev.Code] = window;
                EnsureTicking();
            }

            EventBroadcaster.BroadcastToServer(sapi, minutes > 0
                ? Lang.Get("canevents:signup-opened", ev.Code, TimeText.Short(minutes * 60))
                : Lang.Get("canevents:signup-opened-manual", ev.Code));

            SendInvites(ev, window, isUpdate: false);
            sapi.World.Logger.Notification("SignupWindows: opened '{0}' for {1} minute(s)", ev.Code, minutes);

            message = minutes > 0
                ? Lang.Get("canevents:signup-open-ok", ev.Code, minutes)
                : Lang.Get("canevents:signup-open-ok-manual", ev.Code);
            return true;
        }

        /// <summary>Closes a window without starting anything.</summary>
        public bool Cancel(string eventCode, out string message)
        {
            if (!Close(eventCode, "cancelled"))
            {
                message = Lang.Get("canevents:signup-not-open", eventCode);
                return false;
            }
            EventBroadcaster.BroadcastToServer(sapi, Lang.Get("canevents:signup-cancelled", eventCode));
            message = Lang.Get("canevents:signup-cancel-ok", eventCode);
            return true;
        }

        // Internals

        /// <summary>Drops the window and dismisses its popups. False when none was open.</summary>
        private bool Close(string eventCode, string reason)
        {
            if (string.IsNullOrEmpty(eventCode)) return false;
            string code;
            lock (_lock)
            {
                if (!windows.TryGetValue(eventCode, out var window)) return false;
                code = window.EventCode;
                windows.Remove(eventCode);
                StopTickingIfIdle();
            }

            var net = Net;
            if (net != null)
                foreach (var p in sapi.World.AllOnlinePlayers)
                    if (p is IServerPlayer sp) net.SendEventInviteClosed(sp, code, reason);
            return true;
        }

        private void OnTick(float dt)
        {
            List<SignupWindow> open;
            lock (_lock)
            {
                if (windows.Count == 0) return;
                open = windows.Values.ToList();
            }

            foreach (var window in open)
            {
                var ev = manager.FindEventByCode(window.EventCode);
                // Disabled or removed underneath us: the window has nothing left to fill.
                if (ev == null || !manager.IsEventEnabled(ev.Code))
                {
                    Close(window.EventCode, "cancelled");
                    continue;
                }

                int registered = CountRegistrations(ev);
                int maxParties = ev.AssemblingEB?.MaxPartiesCount ?? 0;

                // Full lobby: stop holding the queue and let the queue manager assemble it the normal
                // way, which respects the mode's own team layout instead of cramming everyone into one
                // match. The window itself stays open until that assembly actually starts a match
                // (OnMatchStarted) or the deadline passes - an assembly can fail, and a window closed
                // here would take the whole sign-up down with it, silently.
                //
                // Recomputed every tick, not latched: someone leaving the queue makes the lobby
                // un-full again, and a hold that never came back would let the match go on the mode's
                // minimum - the very thing the window exists to prevent.
                bool wasFull = window.HoldReleased;
                window.HoldReleased = maxParties > 0 && registered >= maxParties;
                if (window.HoldReleased && !wasFull)
                    EventBroadcaster.BroadcastToServer(sapi, Lang.Get("canevents:signup-full", ev.Code));

                // Only on a change. Nothing is sent to keep a clock moving: the client counts down
                // from the last packet against its own clock.
                if (registered != window.LastRegistered)
                {
                    window.LastRegistered = registered;
                    SendInvites(ev, window, isUpdate: true);
                }

                if (window.EndsUtc == null) continue;   // manual window: only the admin ends it
                if (window.SecondsLeft > 0f) continue;

                // Time is up. A successful start closes the window through OnMatchStarted, with the
                // honest reason; only the failing case is closed here, and it is not "started".
                if (!scheduler.ForceStart(ev.Code, out var reason))
                {
                    Close(window.EventCode, "expired");
                    // A window somebody opened deserves an answer; one the scheduler opened on its own
                    // would just be telling an empty server the same thing every interval.
                    if (!window.Auto)
                        EventBroadcaster.BroadcastToServer(sapi, Lang.Get("canevents:signup-empty", ev.Code));
                    sapi.World.Logger.Notification(
                        "SignupWindows: {0}window for '{1}' expired without a match: {2}",
                        window.Auto ? "auto " : "", ev.Code, reason);
                }
            }
        }

        private void OnPlayerNowPlaying(IServerPlayer player)
        {
            List<SignupWindow> open;
            lock (_lock)
            {
                if (windows.Count == 0) return;
                open = windows.Values.ToList();
            }

            foreach (var window in open)
            {
                var ev = manager.FindEventByCode(window.EventCode);
                if (ev == null) continue;
                SendInvite(player, ev, window, CountRegistrations(ev), isUpdate: false);
            }
        }

        private void SendInvites(CANEvent ev, SignupWindow window, bool isUpdate)
        {
            int registered = CountRegistrations(ev);
            foreach (var p in sapi.World.AllOnlinePlayers)
                if (p is IServerPlayer sp) SendInvite(sp, ev, window, registered, isUpdate);
        }

        private void SendInvite(IServerPlayer player, CANEvent ev, SignupWindow window,
            int registered, bool isUpdate)
        {
            Net?.SendEventInvite(player, new EventInviteMsg
            {
                EventCode = ev.Code,
                Category = ev.Category,
                SecondsLeft = window.SecondsLeft,
                Registered = registered,
                MaxParties = ev.AssemblingEB?.MaxPartiesCount ?? 0,
                YouAreIn = manager.FindRegistration(player.PlayerUID, ev) != null,
                IsUpdate = isUpdate
            });
        }

        private int CountRegistrations(CANEvent ev) => manager.CountRegistrations(ev);

        public void Dispose()
        {
            lock (_lock)
            {
                if (tickListenerId != 0) sapi.Event.UnregisterGameTickListener(tickListenerId);
                tickListenerId = 0;
            }
            sapi.Event.PlayerNowPlaying -= _onPlayerNowPlaying;
            manager.OnMatchStarted -= _onMatchStarted;
        }
    }
}
