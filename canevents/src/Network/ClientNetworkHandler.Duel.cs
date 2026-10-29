using System;
using canevents.src.Duels;

namespace canevents.src.Network
{
    // Client half of duels: the outstanding prompt, and the list of who can be challenged.
    public partial class ClientNetworkHandler
    {
        // The challenge waiting for an answer, or null. The prompt dialog reads this.
        public DuelPromptMsg PendingDuel { get; private set; }

        // Fires when a prompt arrives or is withdrawn.
        public event Action OnDuelPromptChanged;

        // Who this player can challenge right now; refreshed on request.
        public readonly ServerState<DuelTargetListMsg> DuelTargets = new ServerState<DuelTargetListMsg>();

        private void RegisterDuelHandlers()
        {
            SetHandler<DuelPromptMsg>(OnDuelPrompt);
            SetHandler<DuelClosedMsg>(OnDuelClosed);
            SetHandler<DuelTargetListMsg>(DuelTargets.Set);
        }

        private void OnDuelPrompt(DuelPromptMsg msg)
        {
            PendingDuel = msg;
            OnDuelPromptChanged?.Invoke();
        }

        private void OnDuelClosed(DuelClosedMsg msg)
        {
            // Only the prompt being shown: a late close for an older challenge must not wipe a newer
            // one that arrived in the meantime.
            if (msg == null || PendingDuel == null || PendingDuel.ChallengeId != msg.ChallengeId) return;

            PendingDuel = null;
            OnDuelPromptChanged?.Invoke();
        }

        public void SendDuelChallenge(string targetName, DuelKind kind, string wagerItems = null,
            string eventCode = null, string[] mutators = null)
            => Send(new DuelChallengeMsg
            {
                TargetName = targetName,
                Kind = kind,
                WagerItems = wagerItems,
                EventCode = eventCode,
                Mutators = mutators
            });

        public void SendDuelAnswer(bool accept)
        {
            if (PendingDuel == null) return;
            Send(new DuelResponseMsg
            {
                ChallengeId = PendingDuel.ChallengeId,
                Accept = accept
            });
        }

        public void RequestDuelTargets() => Send(new RequestDuelTargetsMsg());
    }
}
