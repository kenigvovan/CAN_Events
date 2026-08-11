using Vintagestory.API.Server;

namespace canevents.src.Network
{
    // Server side of the signup window: pushing invites out. The answer comes back as an ordinary
    // JoinEventRequestMsg - the popup's button is the same sign-up as /canreg, not a second path.
    public partial class ServerNetworkHandler
    {
        public void SendEventInvite(IServerPlayer player, EventInviteMsg msg)
            => serverChannel.SendPacket(msg, player);

        public void SendEventInviteClosed(IServerPlayer player, string eventCode, string reason)
            => serverChannel.SendPacket(new EventInviteClosedMsg
            {
                EventCode = eventCode,
                Reason = reason
            }, player);
    }
}
