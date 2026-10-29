using System;

namespace canevents.src.Network
{
    // Display state only, the server checks permissions.
    public partial class ClientNetworkHandler
    {
        public bool IsObserving { get; private set; }

        public long ObservedEventId { get; private set; }

        // UI hint, mirrors the server's privilege check.
        public bool SpectatePrivileged { get; private set; }

        // null when not observing.
        public SpectatorTelemetryMsg Telemetry { get; private set; }

        // Lets the panel grey out stale data.
        public long TelemetryReceivedMs { get; private set; }

        public event Action OnSpectateStateChanged;

        private void RegisterSpectateHandlers()
        {
            SetHandler<SpectateStateMsg>(OnSpectateState);
            SetHandler<SpectatorTelemetryMsg>(OnSpectatorTelemetry);
            SetHandler<SpectateCameraMsg>(OnSpectateCamera);
        }

        private void OnSpectateCamera(SpectateCameraMsg msg)
        {
            // Never move the camera of someone who is playing.
            if (msg == null || !IsObserving) return;

            capi.Input.MouseYaw = msg.Yaw;
            capi.Input.MousePitch = msg.Pitch;
        }

        private void OnSpectateState(SpectateStateMsg msg)
        {
            if (msg == null) return;

            IsObserving = msg.Observing;
            ObservedEventId = msg.Observing ? msg.EventId : 0;
            SpectatePrivileged = msg.Privileged;
            if (!msg.Observing) Telemetry = null;

            if (!string.IsNullOrEmpty(msg.Message))
                capi.ShowChatMessage(msg.Message);

            OnSpectateStateChanged?.Invoke();
        }

        private void OnSpectatorTelemetry(SpectatorTelemetryMsg msg)
        {
            // Drop packets from a match we stopped watching.
            if (msg == null || msg.EventId != ObservedEventId) return;

            Telemetry = msg;
            TelemetryReceivedMs = capi.ElapsedMilliseconds;
        }

        // eventId 0 = the only running match.
        public void SendSpectateRequest(long eventId)
            => Send(new SpectateRequestMsg { EventId = eventId, Start = true });

        public void SendStopSpectating()
            => Send(new SpectateRequestMsg { Start = false });

        public void SendFollowStep(int step)
        {
            if (!IsObserving) return;
            Send(new SpectateFollowMsg
            {
                EventId = ObservedEventId,
                Step = step
            });
        }
    }
}
