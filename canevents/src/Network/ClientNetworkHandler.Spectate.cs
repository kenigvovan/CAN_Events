using System;

namespace canevents.src.Network
{
    // Client half of spectating. Everything here is display state: what the server last said about
    // this player's seat, and the latest field readout to draw. The server re-checks every request,
    // so nothing on this side is a permission boundary.
    public partial class ClientNetworkHandler
    {
        /// <summary>Is this client currently seated as an observer of a match?</summary>
        public bool IsObserving { get; private set; }

        /// <summary>The match being observed, or 0.</summary>
        public long ObservedEventId { get; private set; }

        /// <summary>May this client use the broadcaster tools? Mirrors the server's privilege check,
        /// so the panel can stay hidden rather than offering buttons that get refused.</summary>
        public bool SpectatePrivileged { get; private set; }

        /// <summary>Latest participant readout, or null when not observing. Replaced wholesale each
        /// packet - the panel reads it every frame and must never see a half-updated roster.</summary>
        public SpectatorTelemetryMsg Telemetry { get; private set; }

        /// <summary>Client clock at the last telemetry packet, so the panel can grey out a readout
        /// that stopped arriving instead of showing stale positions as live.</summary>
        public long TelemetryReceivedMs { get; private set; }

        /// <summary>Fires when the seat is taken or given up.</summary>
        public event Action OnSpectateStateChanged;

        private void RegisterSpectateHandlers()
        {
            SetHandler<SpectateStateMsg>(OnSpectateState);
            SetHandler<SpectatorTelemetryMsg>(OnSpectatorTelemetry);
            SetHandler<SpectateCameraMsg>(OnSpectateCamera);
        }

        private void OnSpectateCamera(SpectateCameraMsg msg)
        {
            // Only while seated: a stray camera packet must not yank the view of someone playing.
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
            // A packet from a match we are no longer watching would resurrect a closed panel.
            if (msg == null || msg.EventId != ObservedEventId) return;

            Telemetry = msg;
            TelemetryReceivedMs = capi.ElapsedMilliseconds;
        }

        /// <summary>Ask to watch a match. <paramref name="eventId"/> 0 means "the only one running".</summary>
        public void SendSpectateRequest(long eventId)
            => clientChannel.SendPacket(new SpectateRequestMsg { EventId = eventId, Start = true });

        /// <summary>Ask to leave the spectator seat.</summary>
        public void SendStopSpectating()
            => clientChannel.SendPacket(new SpectateRequestMsg { Start = false });

        /// <summary>Move the broadcaster camera one participant along the line-up. Stepping is all the
        /// client can ask for: picking a specific fighter needs a clickable surface, and a HUD panel
        /// cannot have one (it is not focusable, so the cursor is never freed over it). That job
        /// belongs to /canspectate follow.</summary>
        public void SendFollowStep(int step)
        {
            if (!IsObserving) return;
            clientChannel.SendPacket(new SpectateFollowMsg
            {
                EventId = ObservedEventId,
                Step = step
            });
        }
    }
}
