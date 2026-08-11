using System;
using System.Collections.Generic;

namespace canevents.src.Network
{
    // Client cache for open registration windows. The server sends a window's state when it changes;
    // the countdown between those packets is worked out here from the client clock, so nothing has to
    // be pushed on a timer just to keep a number moving on screen.
    public partial class ClientNetworkHandler
    {
        private class OpenInvite
        {
            public EventInviteMsg Msg;
            /// <summary>Client clock (ms) when this state arrived - the countdown's zero point.</summary>
            public long ReceivedMs;
            /// <summary>Window length as first seen, so a progress bar has a full scale.</summary>
            public float WindowSeconds;
        }

        // Several events can be taking sign-ups at once; keyed by event code.
        private readonly Dictionary<string, OpenInvite> openInvites =
            new Dictionary<string, OpenInvite>(StringComparer.OrdinalIgnoreCase);

        public event Action<EventInviteMsg> OnEventInvited;
        public event Action<EventInviteClosedMsg> OnEventInviteClosed;

        /// <summary>Is registration open for this event right now?</summary>
        public bool HasOpenSignup(string eventCode)
            => !string.IsNullOrEmpty(eventCode) && openInvites.ContainsKey(eventCode);

        /// <summary>
        /// The invite worth showing: the one closing soonest. A window with no deadline yields to any
        /// window that has one, since only the timed one can be missed.
        ///
        /// One pass, no LINQ: the events browser asks for this every frame, and sorting a list that
        /// almost always holds a single item is work for nothing.
        /// </summary>
        /// <param name="skip">Codes to pass over - the panel uses it for windows the player dismissed.</param>
        public EventInviteMsg SoonestInvite(Func<string, bool> skip = null)
        {
            EventInviteMsg best = null;
            float bestLeft = 0f;

            foreach (var open in openInvites.Values)
            {
                var msg = open.Msg;
                if (msg == null) continue;
                if (skip != null && skip(msg.EventCode)) continue;

                float left = msg.SecondsLeft < 0f ? float.MaxValue : SecondsLeftFor(msg.EventCode);
                if (best != null && left >= bestLeft) continue;

                best = msg;
                bestLeft = left;
            }
            return best;
        }

        /// <summary>
        /// Seconds left on a window, counted from the last packet against the client clock.
        /// -1 for a window with no deadline, 0 when there is none open (or it has run out).
        /// </summary>
        public float SecondsLeftFor(string eventCode)
        {
            if (string.IsNullOrEmpty(eventCode) || !openInvites.TryGetValue(eventCode, out var open)) return 0f;
            if (open.Msg.SecondsLeft < 0f) return -1f;

            float elapsed = (capi.ElapsedMilliseconds - open.ReceivedMs) / 1000f;
            return Math.Max(0f, open.Msg.SecondsLeft - elapsed);
        }

        /// <summary>The full window length, for a progress bar's scale.</summary>
        public float WindowSecondsFor(string eventCode)
            => !string.IsNullOrEmpty(eventCode) && openInvites.TryGetValue(eventCode, out var open)
                ? open.WindowSeconds : 0f;

        private void OnEventInvite(EventInviteMsg msg)
        {
            if (msg == null || string.IsNullOrEmpty(msg.EventCode)) return;

            // A refresh only means something for a window this client knows is open: one arriving after
            // the close packet would put a dead countdown back on screen.
            bool known = openInvites.TryGetValue(msg.EventCode, out var open);
            if (msg.IsUpdate && !known) return;

            if (!known)
            {
                open = new OpenInvite { WindowSeconds = Math.Max(1f, msg.SecondsLeft) };
                openInvites[msg.EventCode] = open;
            }
            open.Msg = msg;
            open.ReceivedMs = capi.ElapsedMilliseconds;

            OnEventInvited?.Invoke(msg);
        }

        // Named apart from the event it raises: a handler and an event cannot share a name.
        private void HandleEventInviteClosed(EventInviteClosedMsg msg)
        {
            if (msg == null || string.IsNullOrEmpty(msg.EventCode)) return;
            openInvites.Remove(msg.EventCode);
            OnEventInviteClosed?.Invoke(msg);
        }
    }
}
