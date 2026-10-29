using Vintagestory.API.Server;

namespace canevents.src.Network
{
    // Server side of the panel's server-settings table: ModConfig/canevents.json, edited in place.
    // The rules (what is offered, ranges, live vs restart) live in ServerConfigEditor.
    public partial class ServerNetworkHandler
    {
        private ServerConfigEditor configEditor;

        // After LoadConfig: the editor snapshots the restart-only values the server started with.
        private void RegisterServerSettingsHandlers()
        {
            configEditor = new ServerConfigEditor(sapi, GlobalConfig);

            const ProtocolChannel admin = ProtocolChannel.Admin;
            Handle<RequestServerSettingsMsg>(admin, true, OnRequestServerSettings);
            Handle<SetServerSettingMsg>(admin, true, OnSetServerSetting);
        }

        private void OnRequestServerSettings(IServerPlayer player, RequestServerSettingsMsg msg)
        {
            if (!RequireAdmin(player)) return;
            adminChannel.SendPacket(configEditor.BuildMsg(), player);
        }

        private void OnSetServerSetting(IServerPlayer player, SetServerSettingMsg msg)
        {
            if (!RequireAdmin(player) || msg == null) return;

            bool ok = configEditor.TryApply(player, msg.Key, msg.Value, out string message);
            Reply(player, ok, message);

            // Echoed either way: a refused value has to snap back to the stored one in the table.
            adminChannel.SendPacket(configEditor.BuildMsg(), player);
            // The Dashboard's scheduler card shows the interval, minimum players and the cap too.
            if (ok) SendScheduleStateTo(player);
        }
    }
}
