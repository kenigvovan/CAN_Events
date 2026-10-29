using System;

namespace canevents.src.Network
{
    // Client half of the queue ready check.
    public partial class ClientNetworkHandler
    {
        // The confirmation waiting for an answer, or null.
        public MatchReadyRequestMsg PendingReady { get; private set; }

        // Fires when the prompt arrives or is withdrawn.
        public event Action OnReadyPromptChanged;

        private void RegisterReadyHandlers()
        {
            SetHandler<MatchReadyRequestMsg>(OnReadyRequest);
            SetHandler<MatchReadyClosedMsg>(OnReadyClosed);
        }

        private void OnReadyRequest(MatchReadyRequestMsg msg)
        {
            PendingReady = msg;
            OnReadyPromptChanged?.Invoke();
        }

        private void OnReadyClosed(MatchReadyClosedMsg msg)
        {
            // Only the prompt on screen: a late close for an older match must not wipe a newer one.
            if (msg == null || PendingReady == null || PendingReady.EventId != msg.EventId) return;

            PendingReady = null;
            OnReadyPromptChanged?.Invoke();
        }

        public void SendMatchReady(bool ready)
        {
            if (PendingReady == null) return;
            Send(new MatchReadyResponseMsg
            {
                EventId = PendingReady.EventId,
                Ready = ready
            });
        }
    }
}
