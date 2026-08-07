using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace canevents.src.Network
{
    // Server-side queue handling: join/leave requests from clients. The work itself (and the
    // follow-ups a sign-up needs) lives in QueueService, shared with /canreg and /leave.
    public partial class ServerNetworkHandler
    {
        private void OnJoinRequest(IServerPlayer player, JoinEventRequestMsg msg)
        {
            // The browser always names the event; only the chat command has a "first event" default.
            if (string.IsNullOrEmpty(msg.EventCode))
            {
                player.SendMessage(0, Lang.Get("canevents:specify-event-code"), EnumChatType.Notification);
                return;
            }

            var (_, message) = eventManager.Queueing.Join(player, msg.EventCode, msg.LoadoutSet);
            player.SendMessage(0, message, EnumChatType.Notification);
        }

        private void OnLeaveRequest(IServerPlayer player, LeaveEventRequestMsg msg)
        {
            var (_, message) = eventManager.Queueing.Leave(player, msg.EventCode);
            player.SendMessage(0, message, EnumChatType.Notification);
        }
    }
}
