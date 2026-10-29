using System.Linq;
using Vintagestory.API.Server;

namespace canevents.src.Network
{
    // Server side of the Events tab's settings card. The admin gate is in Handle too; the checks
    // here keep the handlers safe when called directly.
    public partial class ServerNetworkHandler
    {
        private void RegisterEventSettingsHandlers()
        {
            const ProtocolChannel admin = ProtocolChannel.Admin;
            Handle<RequestEventSettingsMsg>(admin, true, OnRequestEventSettings);
            Handle<SetEventSettingMsg>(admin, true, OnSetEventSetting);
        }

        private void OnRequestEventSettings(IServerPlayer player, RequestEventSettingsMsg msg)
        {
            if (!RequireAdmin(player) || msg == null) return;
            SendEventSettings(player, msg.EventCode);
        }

        private void OnSetEventSetting(IServerPlayer player, SetEventSettingMsg msg)
        {
            if (!RequireAdmin(player) || msg == null) return;

            var (ok, message) = eventManager.SetEventSetting(msg.EventCode, msg.Behavior, msg.Key, msg.Value);
            Reply(player, ok, message);
            // Echoed either way: a refused value has to snap back in the table.
            SendEventSettings(player, msg.EventCode);
        }

        private void SendEventSettings(IServerPlayer player, string code)
        {
            var ev = eventManager.FindEventByCode(code);
            adminChannel.SendPacket(new EventSettingsMsg
            {
                EventCode = ev?.Code ?? code,
                Rows = eventManager.EventSettings(code).Select(s => new EventSettingRowMsg
                {
                    Behavior = s.Behavior,
                    Key = s.Setting.Key,
                    Type = (int)s.Setting.Type,
                    Value = s.Value,
                    Default = s.Default
                }).ToArray()
            }, player);
        }
    }
}
