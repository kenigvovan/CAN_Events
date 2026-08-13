using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace canevents.src.Network
{
    // Per-player access: the spectate privilege and the queue blacklist, both editable from the admin
    // panel. Every handler here is admin-gated at registration (see SetAdminHandler) and re-checks on
    // arrival, because granting privileges from a packet is exactly the thing worth being strict about.
    public partial class ServerNetworkHandler
    {
        /// <summary>Cap on the access list. Past this the panel is the wrong tool anyway, and the
        /// packet would carry a roster nobody reads.</summary>
        private const int MaxAccessEntries = 200;

        private void RegisterAccessHandlers()
        {
            SetAdminHandler<RequestPlayerAccessMsg>((player, _) => SendPlayerAccessList(player));
            SetAdminHandler<PlayerAccessActionMsg>(OnPlayerAccessAction);
        }

        private void OnPlayerAccessAction(IServerPlayer player, PlayerAccessActionMsg msg)
        {
            if (msg == null) return;
            if (!RequireAdmin(player)) return;

            // A name is what the admin types for someone not on the list yet; a uid is what the rows
            // carry. Either resolves to a uid here, or there is nothing to act on.
            string uid = msg.PlayerUid;
            string name = msg.PlayerName;

            if (string.IsNullOrEmpty(uid) && !string.IsNullOrWhiteSpace(name))
            {
                var data = sapi.PlayerData.GetPlayerDataByLastKnownName(name.Trim());
                uid = data?.PlayerUID;
                name = data?.LastKnownPlayername ?? name;
            }

            if (string.IsNullOrEmpty(uid))
            {
                ReplyFail(player, Lang.Get("canevents:access-no-such-player", name ?? "?"));
                return;
            }

            switch (msg.Action)
            {
                case PlayerAccessAction.GrantSpectate:
                    // Permanent: an admin granting a moderator the broadcaster role means it to
                    // survive a restart, not to evaporate with the session.
                    sapi.Permissions.RemovePrivilegeDenial(uid, CanEventsPrivileges.Spectate);
                    sapi.Permissions.GrantPrivilege(uid, CanEventsPrivileges.Spectate, permanent: true);
                    ReplyOk(player, Lang.Get("canevents:access-spectate-granted", NameOf(uid, name)));
                    break;

                case PlayerAccessAction.RevokeSpectate:
                    sapi.Permissions.RevokePrivilege(uid, CanEventsPrivileges.Spectate, permanent: true);
                    // A right that comes from the player's ROLE survives the revoke above - the role
                    // still lists it - so it also has to be denied per player, or the button reads as
                    // broken. Harmless when there was only a personal grant.
                    sapi.Permissions.DenyPrivilege(uid, CanEventsPrivileges.Spectate);

                    // Already watching something on a right they no longer have: seat them out now
                    // rather than leave a live broadcaster feed running for a revoked account.
                    if (sapi.World.PlayerByUid(uid) is IServerPlayer revoked)
                        eventManager.StopObserving(revoked, notifyClient: true);

                    ReplyOk(player, Lang.Get("canevents:access-spectate-revoked", NameOf(uid, name)));
                    break;

                // Through the manager, not the store: blacklisting also has to drop their pending
                // registrations, or a signup already in flight slips past the ban.
                case PlayerAccessAction.BlacklistAdd:
                    eventManager.SetPlayerBlacklisted(uid, true);
                    ReplyOk(player, Lang.Get("canevents:access-blacklisted", NameOf(uid, name)));
                    break;

                case PlayerAccessAction.BlacklistRemove:
                    eventManager.SetPlayerBlacklisted(uid, false);
                    ReplyOk(player, Lang.Get("canevents:access-unblacklisted", NameOf(uid, name)));
                    break;
            }

            SendPlayerAccessList(player);
        }

        // Best display name: what the server last saw, falling back to whatever the caller typed.
        private string NameOf(string uid, string fallback)
            => sapi.PlayerData.GetPlayerDataByUid(uid)?.LastKnownPlayername ?? fallback ?? uid;

        private void SendPlayerAccessList(IServerPlayer player)
        {
            var entries = new List<PlayerAccessEntryMsg>();
            var seen = new HashSet<string>();
            int omitted = 0;

            // Online first: those are the ones an admin is usually acting on.
            foreach (var online in sapi.World.AllOnlinePlayers.OfType<IServerPlayer>())
            {
                if (!seen.Add(online.PlayerUID)) continue;
                entries.Add(BuildAccessEntry(online.PlayerUID, online.PlayerName, true));
            }

            // Then anyone offline who already has a decision recorded against them - otherwise a
            // grant made yesterday would be invisible and impossible to take back from the panel.
            foreach (var kv in sapi.PlayerData.PlayerDataByUid)
            {
                if (seen.Contains(kv.Key)) continue;
                if (!HasSpectateRight(kv.Key) && !eventManager.IsPlayerBlacklisted(kv.Key)) continue;

                if (entries.Count >= MaxAccessEntries) { omitted++; continue; }

                seen.Add(kv.Key);
                entries.Add(BuildAccessEntry(kv.Key, kv.Value?.LastKnownPlayername, false));
            }

            serverChannel.SendPacket(new PlayerAccessListMsg
            {
                Players = entries.ToArray(),
                OmittedCount = omitted
            }, player);
        }

        private PlayerAccessEntryMsg BuildAccessEntry(string uid, string name, bool online)
        {
            var data = sapi.PlayerData.GetPlayerDataByUid(uid);

            return new PlayerAccessEntryMsg
            {
                PlayerUid = uid,
                PlayerName = name ?? data?.LastKnownPlayername ?? uid,
                Online = online,
                MaySpectate = HasSpectateRight(uid),
                SpectateFromRole = RoleGrantsSpectate(data),
                QueueBlacklisted = eventManager.IsPlayerBlacklisted(uid),
                RoleCode = data?.RoleCode ?? ""
            };
        }

        // Does this player have the spectate right, from any source? Mirrors what HasPrivilege would
        // answer, but works for someone offline too - which is the whole point of the offline rows.
        private bool HasSpectateRight(string uid)
        {
            var data = sapi.PlayerData.GetPlayerDataByUid(uid);
            if (data == null) return false;

            if (data.DeniedPrivileges != null
                && data.DeniedPrivileges.Contains(CanEventsPrivileges.Spectate)) return false;

            if (data.PermaPrivileges != null
                && data.PermaPrivileges.Contains(CanEventsPrivileges.Spectate)) return true;

            return RoleGrantsSpectate(data);
        }

        private bool RoleGrantsSpectate(IServerPlayerData data)
        {
            if (string.IsNullOrEmpty(data?.RoleCode)) return false;

            var role = sapi.Permissions.GetRole(data.RoleCode);
            if (role == null) return false;

            return (role.Privileges != null && role.Privileges.Contains(CanEventsPrivileges.Spectate))
                || (role.RuntimePrivileges != null
                    && role.RuntimePrivileges.Contains(CanEventsPrivileges.Spectate));
        }
    }
}
