using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers;
using Vintagestory.API.Server;

namespace canevents.src.Network
{
    public partial class ServerNetworkHandler
    {
        private const int MaxAuditRows = 300;

        private const int MaxAuditHistory = 300;

        private void RegisterInventoryAuditHandlers()
        {
            Handle<RequestInventoryAuditMsg>(ProtocolChannel.Admin, true, OnRequestInventoryAudit);
            Handle<RequestInventoryBackupDetailMsg>(ProtocolChannel.Admin, true, OnRequestInventoryBackupDetail);
            Handle<InventoryBackupActionMsg>(ProtocolChannel.Admin, true, OnInventoryBackupAction);
        }

        // For /eventadmin inv gui.
        public void OpenInventoryAuditFor(IServerPlayer player)
        {
            if (player == null || !RequireAdmin(player)) return;
            adminChannel.SendPacket(new OpenInventoryAuditMsg(), player);
        }

        private void OnRequestInventoryAudit(IServerPlayer player, RequestInventoryAuditMsg msg)
        {
            if (!RequireAdmin(player)) return;

            string filter = string.IsNullOrWhiteSpace(msg?.NameFilter) ? null : msg.NameFilter.Trim();
            bool everyone = msg?.IncludeEveryone ?? false;

            var rows = new List<InventoryAuditRowMsg>();
            int scanned = 0, unreadable = 0;
            // Save reads are budgeted per sweep, see InventorySnapshots.Sweep.
            var sweep = new InventorySnapshots.Sweep(sapi);

            foreach (var (uid, name) in PlayerModDataAccess.KnownPlayers(sapi))
            {
                if (filter != null && (name == null
                    || name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)) continue;

                scanned++;
                if (rows.Count >= MaxAuditRows) continue;

                var row = BuildAuditRow(uid, name, sweep.Read(uid));

                if (row.Unreadable)
                {
                    unreadable++;
                    // Only counted unless filtering; the read budget makes many of these on big servers.
                    if (filter == null && !everyone) continue;
                }
                // Default view: only players holding a snapshot.
                else if (!everyone && !row.HasActive && !row.HasParked) continue;

                rows.Add(row);
            }

            var history = Array.Empty<InventoryAuditHistoryMsg>();
            int limit = Math.Clamp(msg?.HistoryLimit ?? 0, 0, MaxAuditHistory);
            if (limit > 0)
            {
                history = InventoryAuditLog.Recent(limit, msg?.HistoryUid)
                    .Select(e => new InventoryAuditHistoryMsg
                    {
                        AtUnix = e.AtUnix,
                        Kind = (int)e.Kind,
                        PlayerUid = e.PlayerUid,
                        PlayerName = e.PlayerName,
                        EventCode = e.EventCode,
                        StackCount = e.StackCount,
                        Detail = e.Detail
                    }).ToArray();
            }

            adminChannel.SendPacket(new InventoryAuditListMsg
            {
                Rows = rows.ToArray(),
                History = history,
                AuditEnabled = InventoryAuditLog.Enabled,
                PlayersChecked = scanned,
                Unreadable = unreadable
            }, player);
        }

        private InventoryAuditRowMsg BuildAuditRow(string uid, string name, SnapshotPair pair)
        {
            var active = pair.Active;
            var parked = pair.Parked;

            return new InventoryAuditRowMsg
            {
                PlayerUid = uid,
                PlayerName = name,
                Online = sapi.World.PlayerByUid(uid) is IServerPlayer,
                InMatch = eventManager?.FindLiveEventOf(uid) != null,

                HasActive = active.Present,
                ActiveStacks = active.StackCount,
                ActiveEventCode = active.EventCode,
                ActiveAgeSeconds = active.AgeSeconds,
                ActiveKeepLive = active.KeepLiveInventory,

                HasParked = parked.Present,
                ParkedStacks = parked.StackCount,
                ParkedEventCode = parked.EventCode,
                ParkedAgeSeconds = parked.AgeSeconds,

                Unreadable = pair.Unreadable,
                UnsettledInHistory = InventoryAuditLog.HasUnsettledTake(uid)
            };
        }

        private void OnRequestInventoryBackupDetail(IServerPlayer player, RequestInventoryBackupDetailMsg msg)
        {
            if (!RequireAdmin(player) || msg == null || string.IsNullOrEmpty(msg.PlayerUid)) return;

            var info = InventorySnapshots.Read(sapi, msg.PlayerUid, msg.Parked, out _);

            adminChannel.SendPacket(new InventoryBackupDetailMsg
            {
                PlayerUid = msg.PlayerUid,
                PlayerName = PlayerModDataAccess.NameOf(sapi, msg.PlayerUid),
                Parked = msg.Parked,
                Present = info.Present,
                SavedAtUnix = info.SavedAtUnix,
                EventCode = info.EventCode,
                StackCount = info.StackCount,
                KeepLive = info.KeepLiveInventory,
                Health = info.Health,
                MaxHealth = info.MaxHealth,
                ItemCodes = info.Items.Select(i => i.code).ToArray(),
                Quantities = info.Items.Select(i => i.quantity).ToArray(),
                Resolved = info.Items.Select(i => i.resolved).ToArray()
            }, player);
        }

        private void OnInventoryBackupAction(IServerPlayer player, InventoryBackupActionMsg msg)
        {
            if (!RequireAdmin(player) || msg == null || string.IsNullOrEmpty(msg.PlayerUid)) return;

            if (msg.Action != InventoryBackupAction.RestoreParked) return;

            if (!InventorySnapshots.TryOnlineForRestore(sapi, msg.PlayerUid, out var target, out var offline))
            {
                ReplyFail(player, offline);
                return;
            }

            var (ok, _, message) = PlayerInventoryBackup.RestoreParked(target, sapi, player.PlayerName);
            Reply(player, ok, message);

            OnRequestInventoryAudit(player, new RequestInventoryAuditMsg { HistoryLimit = 50 });
        }
    }
}
