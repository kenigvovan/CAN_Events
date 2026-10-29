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
    public class SignupWindow
    {
        public string EventCode;
        // null = closed by the admin only.
        public DateTime? EndsUtc;
        public int LastRegistered = -1;

        // Opened by the scheduler; expires silently.
        public bool Auto;

        // Lobby full: QM may assemble, but the window stays open in case assembly fails.
        public bool HoldReleased;

        public float SecondsLeft => EndsUtc == null
            ? -1f
            : Math.Max(0f, (float)(EndsUtc.Value - DateTime.UtcNow).TotalSeconds);
    }

    // Holds back queue assembly until the deadline, a full lobby or the admin. Packets go out only
    // on change; the client counts down itself.
    public class SignupWindows
    {
        public const int MaxWindowMinutes = 24 * 60;
        // Used by the scheduler when the configured length is 0.
        public const int DefaultAutoWindowMinutes = 5;

        private readonly ICoreServerAPI sapi;
        private readonly CANEventManager manager;
        private readonly object _lock = new object();

        private readonly Dictionary<string, SignupWindow> windows =
            new Dictionary<string, SignupWindow>(StringComparer.OrdinalIgnoreCase);

        // 0 while no window is open. Guarded by _lock.
        private long tickListenerId;
        private readonly PlayerDelegate _onPlayerNowPlaying;
        private readonly Action<RunningEvent, MatchStartSource> _onMatchStarted;

        public SignupWindows(ICoreServerAPI sapi, CANEventManager manager)
        {
            this.sapi = sapi;
            this.manager = manager;

            _onPlayerNowPlaying = OnPlayerNowPlaying;
            sapi.Event.PlayerNowPlaying += _onPlayerNowPlaying;

            // External (tournament) starts don't take anyone out of the queue, so the window stays.
            _onMatchStarted = (re, source) =>
            {
                if (source == MatchStartSource.External) return;
                Close(re?.Event?.Code, "started");
            };
            manager.OnMatchStarted += _onMatchStarted;
        }

        // Tick only while a window is open. Call with _lock held.
        private void EnsureTicking()
        {
            if (tickListenerId == 0) tickListenerId = sapi.Event.RegisterGameTickListener(OnTick, 1000);
        }

        private void StopTickingIfIdle()
        {
            if (windows.Count > 0 || tickListenerId == 0) return;
            sapi.Event.UnregisterGameTickListener(tickListenerId);
            tickListenerId = 0;
        }

        private ServerNetworkHandler Net => manager.NetworkHandler;

        public bool IsHolding(string eventCode)
        {
            if (string.IsNullOrEmpty(eventCode)) return false;
            lock (_lock)
                return windows.TryGetValue(eventCode, out var window) && !window.HoldReleased;
        }

        public List<(string code, float secondsLeft)> OpenWindows()
        {
            lock (_lock)
                return windows.Values.Select(w => (w.EventCode, w.SecondsLeft)).ToList();
        }

        // minutes 0 = no deadline, closed by /eventadmin start or cancel.
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
            if (manager.QueueManagers == null || !manager.QueueManagers.ContainsKey(ev.Code))
            {
                message = Lang.Get("canevents:signup-no-queue-manager", ev.Code);
                return false;
            }

            // Don't clamp to 0, that means open-ended.
            if (minutes < 0)
            {
                message = Lang.Get("canevents:signup-bad-minutes", MaxWindowMinutes);
                return false;
            }
            // Nobody would close an open-ended auto window.
            if (auto && minutes == 0) minutes = DefaultAutoWindowMinutes;
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
                if (ev == null || !manager.IsEventEnabled(ev.Code))
                {
                    Close(window.EventCode, "cancelled");
                    continue;
                }

                int registered = CountRegistrations(ev);
                int maxParties = ev.AssemblingEB?.MaxPartiesCount ?? 0;

                // Not latched: someone leaving makes the lobby un-full again.
                bool wasFull = window.HoldReleased;
                window.HoldReleased = maxParties > 0 && registered >= maxParties;
                if (window.HoldReleased && !wasFull)
                    EventBroadcaster.BroadcastToServer(sapi, Lang.Get("canevents:signup-full", ev.Code));

                if (registered != window.LastRegistered)
                {
                    window.LastRegistered = registered;
                    SendInvites(ev, window, isUpdate: true);
                }

                if (window.EndsUtc == null) continue;
                if (window.SecondsLeft > 0f) continue;

                // QM, not force-start, so solo sign-ups aren't each their own side. A start closes via OnMatchStarted.
                int started = manager.AssembleQueuedNow(ev);
                if (started > 0 || manager.HasPendingReadyCheck(ev.Code)) continue;

                Close(window.EventCode, "expired");
                if (!window.Auto)
                    EventBroadcaster.BroadcastToServer(sapi, Lang.Get(
                        registered > 0 ? "canevents:signup-too-few" : "canevents:signup-empty", ev.Code));
                sapi.World.Logger.Notification(
                    "SignupWindows: {0}window for '{1}' expired without a match ({2} sign-up(s))",
                    window.Auto ? "auto " : "", ev.Code, registered);
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
            bool youAreIn = manager.FindRegistration(player.PlayerUID, ev) != null;
            // Quiet mode still shows the countdown to those signed up.
            if (!youAreIn && AnnouncementPrefs.IsQuiet(player)) return;

            Net?.SendEventInvite(player, new EventInviteMsg
            {
                EventCode = ev.Code,
                Category = ev.Category,
                SecondsLeft = window.SecondsLeft,
                Registered = registered,
                MaxParties = ev.AssemblingEB?.MaxPartiesCount ?? 0,
                YouAreIn = youAreIn,
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
