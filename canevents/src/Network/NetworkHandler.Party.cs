using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace canevents.src.Network
{
    // Server-side party handling: wiring PartyManager events, pushing party state to clients,
    // and processing PartyActionMsg from clients.
    public partial class ServerNetworkHandler
    {
        private canparty.PartyManager _partyManager;

        public void WirePartyManager(canparty.PartyManager pm)
        {
            _partyManager = pm;
            pm.OnPartyChanged += OnPartyChanged;
            pm.OnInviteCreated += OnInviteCreated;
            pm.OnInviteRemoved += OnInviteRemoved;
            pm.OnPartyDisbanded += OnPartyDisbanded;
        }

        // From Dispose: otherwise a hot-reload leaves the old handler pushing through a dead channel.
        private void UnwirePartyManager()
        {
            if (_partyManager == null) return;

            _partyManager.OnPartyChanged -= OnPartyChanged;
            _partyManager.OnInviteCreated -= OnInviteCreated;
            _partyManager.OnInviteRemoved -= OnInviteRemoved;
            _partyManager.OnPartyDisbanded -= OnPartyDisbanded;
            _partyManager = null;
        }

        private void OnPartyChanged(canparty.PlayerParty party)
        {
            foreach (var uid in party.playersGuids)
            {
                if (sapi.World.PlayerByUid(uid) is IServerPlayer pl) SendPartyState(pl);
            }
        }

        private void OnPartyDisbanded(canparty.PlayerParty party, string lastLeaderUid)
        {
            // Tell every former member their state is now empty.
            foreach (var uid in party.playersGuids)
            {
                if (sapi.World.PlayerByUid(uid) is IServerPlayer pl) SendPartyState(pl);
            }
        }

        private void OnInviteCreated(canparty.PartyInvite invite)
        {
            if (sapi.World.PlayerByUid(invite.RecipientUid) is IServerPlayer pl) SendPartyState(pl);
        }

        private void OnInviteRemoved(string recipientUid, string partyGuid)
        {
            if (sapi.World.PlayerByUid(recipientUid) is IServerPlayer pl) SendPartyState(pl);
        }

        public void SendPartyState(IServerPlayer player)
        {
            if (_partyManager == null) return;

            var msg = new PartyStateMsg { SelfUid = player.PlayerUID, MaxSize = canparty.PlayerParty.MaxSize };

            if (_partyManager.GetPlayerParty(player, out var party))
            {
                msg.InParty = true;
                msg.PartyGuid = party.partyGuid;
                msg.LeaderUid = party.leaderGuid;
                var members = new List<PartyMemberMsg>();
                foreach (var uid in party.playersGuids)
                {
                    var pl = sapi.World.PlayerByUid(uid);
                    bool online = pl is IServerPlayer sp && sp.ConnectionState == EnumClientState.Playing;
                    members.Add(new PartyMemberMsg
                    {
                        Uid = uid,
                        Name = pl?.PlayerName ?? uid,
                        IsLeader = uid == party.leaderGuid,
                        Online = online
                    });
                }
                msg.Members = members.ToArray();
            }
            else
            {
                msg.InParty = false;
                msg.Members = new PartyMemberMsg[0];
            }

            var invites = _partyManager.GetPendingInvitesForPlayer(player.PlayerUID);
            msg.PendingInvitesForMe = invites.Select(i => new PartyInviteMsg
            {
                PartyGuid = i.PartyGuid,
                InviterName = i.InviterName,
                SecondsRemaining = i.SecondsRemaining
            }).ToArray();

            serverChannel.SendPacket(msg, player);
        }

        private IServerPlayer FindOnlinePlayerByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var pl in sapi.World.AllOnlinePlayers)
            {
                if (string.Equals(pl.PlayerName, name, StringComparison.OrdinalIgnoreCase)) return pl as IServerPlayer;
            }
            return null;
        }

        private void OnPartyAction(IServerPlayer player, PartyActionMsg msg)
        {
            if (_partyManager == null) return;
            (bool ok, string text) = (false, "");
            switch (msg.Action)
            {
                case PartyAction.Create:
                    ok = _partyManager.CreateParty(player);
                    text = ok ? Lang.Get("canevents:party-created") : Lang.Get("canevents:party-create-failed");
                    break;
                case PartyAction.Invite:
                {
                    var target = FindOnlinePlayerByName(msg.TargetName);
                    if (target == null) { ok = false; text = Lang.Get("canevents:party-player-offline"); break; }
                    var r = _partyManager.Invite(player, target);
                    ok = r.ok; text = r.message;
                    if (ok) target.SendMessage(0, Lang.Get("canevents:party-invite-received", player.PlayerName), EnumChatType.Notification);
                    break;
                }
                case PartyAction.Accept:
                {
                    var invites = _partyManager.GetPendingInvitesForPlayer(player.PlayerUID);
                    var inv = invites.FirstOrDefault(i => string.Equals(i.InviterName, msg.InviterName, StringComparison.OrdinalIgnoreCase));
                    if (inv == null) { ok = false; text = Lang.Get("canevents:party-no-invite", msg.InviterName); break; }
                    var r = _partyManager.AcceptInvite(player, inv.PartyGuid);
                    ok = r.ok; text = r.message;
                    break;
                }
                case PartyAction.Decline:
                {
                    var invites = _partyManager.GetPendingInvitesForPlayer(player.PlayerUID);
                    var inv = invites.FirstOrDefault(i => string.Equals(i.InviterName, msg.InviterName, StringComparison.OrdinalIgnoreCase));
                    if (inv == null) { ok = false; text = Lang.Get("canevents:party-no-invite", msg.InviterName); break; }
                    var r = _partyManager.DeclineInvite(player, inv.PartyGuid);
                    ok = r.ok; text = r.message;
                    break;
                }
                case PartyAction.Leave:
                {
                    var r = _partyManager.LeaveParty(player);
                    ok = r.ok; text = r.message;
                    break;
                }
                case PartyAction.Kick:
                {
                    // Leader check on our side, like Disband: the party system is a separate mod, so
                    // this handler enforces the invariant rather than assuming it across the boundary.
                    if (!_partyManager.GetPlayerParty(player, out var kickParty) || kickParty.leaderGuid != player.PlayerUID)
                    {
                        ok = false; text = Lang.Get("canevents:party-kick-leader-only");
                        break;
                    }
                    var r = _partyManager.KickByName(player, msg.TargetName);
                    ok = r.ok; text = r.message;
                    break;
                }
                case PartyAction.Disband:
                {
                    if (!_partyManager.GetPlayerParty(player, out var pty) || pty.leaderGuid != player.PlayerUID)
                    {
                        ok = false; text = Lang.Get("canevents:party-disband-leader-only");
                    }
                    else
                    {
                        ok = _partyManager.DisbandParty(pty);
                        text = ok ? Lang.Get("canevents:party-deleted") : Lang.Get("canevents:party-disband-failed");
                    }
                    break;
                }
                case PartyAction.RequestState:
                    SendPartyState(player);
                    return;
                default:
                    text = Lang.Get("canevents:party-unknown-action");
                    break;
            }

            serverChannel.SendPacket(new PartyOperationResultMsg { Success = ok, Message = text }, player);
            // Always refresh requester's state so failed actions don't leave stale GUI.
            SendPartyState(player);
        }
    }
}
