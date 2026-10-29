using System;
using System.Collections.Generic;

namespace canevents.src.Network
{
    // Open signup windows. Server sends changes only, countdown runs on the client clock.
    public partial class ClientNetworkHandler
    {
        private class OpenInvite
        {
            public EventInviteMsg Msg;
            public long ReceivedMs;
            // Length as first seen, used as the progress bar scale.
            public float WindowSeconds;
        }

        // Keyed by event code.
        private readonly Dictionary<string, OpenInvite> openInvites =
            new Dictionary<string, OpenInvite>(StringComparer.OrdinalIgnoreCase);

        public event Action<EventInviteMsg> OnEventInvited;
        public event Action<EventInviteClosedMsg> OnEventInviteClosed;

        public bool HasOpenSignup(string eventCode)
            => !string.IsNullOrEmpty(eventCode) && openInvites.ContainsKey(eventCode);

        // Soonest to close; windows without a deadline come last. Called every frame, so no LINQ.
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

        // -1 = no deadline, 0 = not open or expired.
        public float SecondsLeftFor(string eventCode)
        {
            if (string.IsNullOrEmpty(eventCode) || !openInvites.TryGetValue(eventCode, out var open)) return 0f;
            if (open.Msg.SecondsLeft < 0f) return -1f;

            float elapsed = (capi.ElapsedMilliseconds - open.ReceivedMs) / 1000f;
            return Math.Max(0f, open.Msg.SecondsLeft - elapsed);
        }

        public float WindowSecondsFor(string eventCode)
            => !string.IsNullOrEmpty(eventCode) && openInvites.TryGetValue(eventCode, out var open)
                ? open.WindowSeconds : 0f;

        private void OnEventInvite(EventInviteMsg msg)
        {
            if (msg == null || string.IsNullOrEmpty(msg.EventCode)) return;

            // Ignore updates for windows we don't have, e.g. ones arriving after the close packet.
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

        private void HandleEventInviteClosed(EventInviteClosedMsg msg)
        {
            if (msg == null || string.IsNullOrEmpty(msg.EventCode)) return;
            openInvites.Remove(msg.EventCode);
            OnEventInviteClosed?.Invoke(msg);
        }
    }
}
