using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace canevents.src.Network
{
    // Spectate privilege and queue blacklist from the admin panel.
    public partial class ServerNetworkHandler
    {
        private const int MaxAccessEntries = 200;

        private void RegisterAccessHandlers()
        {
            Handle<RequestPlayerAccessMsg>(ProtocolChannel.Admin, true, (player, _) => SendPlayerAccessList(player));
            Handle<PlayerAccessActionMsg>(ProtocolChannel.Admin, true, OnPlayerAccessAction);
        }

        private void OnPlayerAccessAction(IServerPlayer player, PlayerAccessActionMsg msg)
        {
            if (msg == null) return;
            if (!RequireAdmin(player)) return;

            // uid from a list row, or a typed name.
            string uid = msg.PlayerUid;
            string name = msg.PlayerName;

            if (string.IsNullOrEmpty(uid) && !string.IsNullOrWhiteSpace(name))
                uid = EventManagers.PlayerModDataAccess.UidOfName(sapi, name);

            if (string.IsNullOrEmpty(uid))
            {
                ReplyFail(player, Lang.Get("canevents:access-no-such-player", name ?? "?"));
                return;
            }

            switch (msg.Action)
            {
                case PlayerAccessAction.GrantSpectate:
                    sapi.Permissions.RemovePrivilegeDenial(uid, CanEventsPrivileges.Spectate);
                    sapi.Permissions.GrantPrivilege(uid, CanEventsPrivileges.Spectate, permanent: true);
                    ReplyOk(player, Lang.Get("canevents:access-spectate-granted", NameOf(uid, name)));
                    break;

                case PlayerAccessAction.RevokeSpectate:
                    sapi.Permissions.RevokePrivilege(uid, CanEventsPrivileges.Spectate, permanent: true);
                    // Deny too, in case the right comes from their role.
                    sapi.Permissions.DenyPrivilege(uid, CanEventsPrivileges.Spectate);

                    if (sapi.World.PlayerByUid(uid) is IServerPlayer revoked)
                        eventManager.StopObserving(revoked, notifyClient: true);

                    ReplyOk(player, Lang.Get("canevents:access-spectate-revoked", NameOf(uid, name)));
                    break;

                // Via the manager so pending registrations get dropped too.
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

        // Falls back to the typed name for unknown uids.
        private string NameOf(string uid, string fallback)
        {
            string known = EventManagers.PlayerModDataAccess.NameOf(sapi, uid);
            return known == uid && fallback != null ? fallback : known;
        }

        private void SendPlayerAccessList(IServerPlayer player)
        {
            var entries = new List<PlayerAccessEntryMsg>();
            var seen = new HashSet<string>();
            int omitted = 0;

            foreach (var online in sapi.World.AllOnlinePlayers.OfType<IServerPlayer>())
            {
                if (!seen.Add(online.PlayerUID)) continue;
                entries.Add(BuildAccessEntry(online.PlayerUID, online.PlayerName, true));
            }

            // Offline players only if they have a grant or a ban.
            foreach (var kv in sapi.PlayerData.PlayerDataByUid)
            {
                if (seen.Contains(kv.Key)) continue;
                if (!HasSpectateRight(kv.Key) && !eventManager.IsPlayerBlacklisted(kv.Key)) continue;

                if (entries.Count >= MaxAccessEntries) { omitted++; continue; }

                seen.Add(kv.Key);
                entries.Add(BuildAccessEntry(kv.Key, kv.Value?.LastKnownPlayername, false));
            }

            adminChannel.SendPacket(new PlayerAccessListMsg
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

        // Like HasPrivilege, but works for offline players.
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
