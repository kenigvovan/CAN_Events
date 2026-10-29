using System;

namespace canevents.src.Network
{
    // Client half of the waiting-room side pick.
    public partial class ClientNetworkHandler
    {
        // Null when no waiting room is open for us.
        public TeamPickStateMsg TeamPick { get; private set; }

        public event Action OnTeamPickChanged;

        private void RegisterTeamPickHandlers()
        {
            SetHandler<TeamPickStateMsg>(OnTeamPickState);
        }

        private void OnTeamPickState(TeamPickStateMsg msg)
        {
            if (msg == null) return;
            // A late close for an older match must not take down a newer one.
            if (msg.Closed && (TeamPick == null || TeamPick.EventId != msg.EventId)) return;

            TeamPick = msg.Closed ? null : msg;
            OnTeamPickChanged?.Invoke();
        }

        public void SendTeamPick(int team)
        {
            if (TeamPick == null) return;
            Send(new TeamPickResponseMsg { EventId = TeamPick.EventId, Team = team });
        }
    }
}
