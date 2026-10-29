using System;
using Vintagestory.API.Client;

namespace canevents.src.Network
{
    // Client side of the inventory audit panel: the overview, one snapshot's contents, and the
    // server's request to open the window.
    public partial class ClientNetworkHandler
    {
        // Rows + history, null until asked for.
        public readonly ServerState<InventoryAuditListMsg> InventoryAudit
            = new ServerState<InventoryAuditListMsg>();

        // One at a time on purpose: caching every snapshot clicked would keep other people's
        // inventories in memory for the session.
        public readonly ServerState<InventoryBackupDetailMsg> InventoryBackupDetail
            = new ServerState<InventoryBackupDetailMsg>();

        // Server asks for the window (/eventadmin inv gui).
        public event Action OnInventoryAuditRequested;

        // These travel on the admin channel (CanEventsProtocol); SetHandler and Send pick it from there.
        private void RegisterInventoryAuditHandlers()
        {
            SetHandler<InventoryAuditListMsg>(InventoryAudit.Set);
            SetHandler<InventoryBackupDetailMsg>(InventoryBackupDetail.Set);
            SetHandler<OpenInventoryAuditMsg>(_ => OnInventoryAuditRequested?.Invoke());
        }

        // History comes with it: a second round trip would leave the panel half-filled for a frame.
        public void RequestInventoryAudit(string nameFilter = null, bool includeEveryone = false,
            int historyLimit = 100, string historyUid = null)
        {
            Send(new RequestInventoryAuditMsg
            {
                NameFilter = nameFilter,
                IncludeEveryone = includeEveryone,
                HistoryLimit = historyLimit,
                HistoryUid = historyUid
            });
        }

        public void RequestInventoryBackupDetail(string uid, bool parked)
        {
            Send(new RequestInventoryBackupDetailMsg
            {
                PlayerUid = uid,
                Parked = parked
            });
        }

        public void SendRestoreParkedBackup(string uid)
        {
            Send(new InventoryBackupActionMsg
            {
                Action = InventoryBackupAction.RestoreParked,
                PlayerUid = uid
            });
        }
    }
}
