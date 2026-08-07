using System;

namespace canevents.src.Network
{
    // Party state and the actions a client can take on it. Every action goes out as one
    // PartyActionMsg with a discriminator; the named wrappers below are call-site sugar.
    public partial class ClientNetworkHandler
    {
        public readonly ServerState<PartyStateMsg> PartyState = new ServerState<PartyStateMsg>();
        public PartyOperationResultMsg LastPartyResult { get; private set; }
        public event Action<PartyOperationResultMsg> OnPartyResultReceived;

        private void OnPartyResult(PartyOperationResultMsg msg)
        {
            LastPartyResult = msg;
            OnPartyResultReceived?.Invoke(msg);
        }

        public void SendPartyAction(PartyAction action, string targetName = null, string inviterName = null)
        {
            clientChannel.SendPacket(new PartyActionMsg { Action = action, TargetName = targetName, InviterName = inviterName });
        }

        public void RequestPartyState() => SendPartyAction(PartyAction.RequestState);
        public void SendPartyCreate() => SendPartyAction(PartyAction.Create);
        public void SendPartyInvite(string targetName) => SendPartyAction(PartyAction.Invite, targetName);
        public void SendPartyAccept(string inviterName) => SendPartyAction(PartyAction.Accept, inviterName: inviterName);
        public void SendPartyDecline(string inviterName) => SendPartyAction(PartyAction.Decline, inviterName: inviterName);
        public void SendPartyLeave() => SendPartyAction(PartyAction.Leave);
        public void SendPartyKick(string targetName) => SendPartyAction(PartyAction.Kick, targetName);
        public void SendPartyDisband() => SendPartyAction(PartyAction.Disband);
    }
}
