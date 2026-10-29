using System.Linq;
using canevents.src.EventManagers;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace canevents.src.Network
{
    // Waiting-room side pick: the state out, the choice back. Rules live in WaitingRoomEB.
    public partial class ServerNetworkHandler
    {
        private void RegisterTeamPickHandlers()
        {
            Handle<TeamPickResponseMsg>(ProtocolChannel.Main, false, OnTeamPickResponse, minIntervalMs: 250);
        }

        private void OnTeamPickResponse(IServerPlayer player, TeamPickResponseMsg msg)
        {
            if (msg == null) return;
            var re = eventManager.FindRunningEvent(msg.EventId);
            var room = re?.GetBehavior<EventManagers.eventBehavior.WaitingRoomEB>();
            if (room == null) return;

            // Same reporting as a side zone, so both paths read alike in chat.
            var (_, message) = room.TryPick(player, msg.Team);
            if (message != null) player.SendMessage(0, message, EnumChatType.Notification);
        }

        public void SendTeamPickState(RunningEvent re, IServerPlayer player, float secondsLeft, int maxImbalance,
            bool closed)
        {
            if (re == null || player == null) return;
            var groups = re.ParticipantsByGroupsBefore;
            var members = new string[groups.Count];
            // Active players only: the counts must match what WaitingRoomEB.TryPick enforces.
            for (int t = 0; t < groups.Count; t++)
                members[t] = string.Join("\n", re.TeamChoices
                    .Where(kv => kv.Value == t && re.IsActive(kv.Key))
                    .Select(kv => re.NameOf(kv.Key)));

            serverChannel.SendPacket(new TeamPickStateMsg
            {
                EventId = re.Id,
                EventCode = re.Event?.Code ?? "",
                SecondsLeft = secondsLeft,
                MaxImbalance = maxImbalance,
                Members = members,
                YourTeam = re.TeamChoices.TryGetValue(player.PlayerUID, out int mine) ? mine : -1,
                Closed = closed
            }, player);
        }
    }
}
