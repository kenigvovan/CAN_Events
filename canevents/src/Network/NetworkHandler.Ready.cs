using canevents.src.EventManagers;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace canevents.src.Network
{
    // Ready check for queue matches: the prompt out, the answer back.
    public partial class ServerNetworkHandler
    {
        private void RegisterReadyHandlers()
        {
            Handle<MatchReadyResponseMsg>(ProtocolChannel.Main, false, OnMatchReadyResponse);
        }

        private void OnMatchReadyResponse(IServerPlayer player, MatchReadyResponseMsg msg)
        {
            if (msg == null) return;

            var (_, message) = eventManager.SetMatchReady(player, msg.EventId, msg.Ready);
            player.SendMessage(0, message, EnumChatType.Notification);
        }

        public void SendMatchReadyRequest(IServerPlayer player, RunningEvent re, int timeoutSeconds)
        {
            if (player == null || re == null) return;

            serverChannel.SendPacket(new MatchReadyRequestMsg
            {
                EventId = re.Id,
                EventCode = re.Event?.Code ?? "",
                TimeoutSeconds = timeoutSeconds,
                TotalPlayers = re.ParticipantsBefore.Count
            }, player);
        }

        public void SendMatchReadyClosed(IServerPlayer player, long eventId)
        {
            if (player == null) return;
            serverChannel.SendPacket(new MatchReadyClosedMsg { EventId = eventId }, player);
        }
    }
}
