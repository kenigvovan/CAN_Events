using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace canevents.src.Network
{
    // Protocol check: on join the server sends ProtocolHandshakeMsg, the client answers with its version.
    // Until then it is "unconfirmed" and admin packets are refused. A wrong answer, or none (an older
    // client), marks it "mismatched": its message ids mean different things here, so nothing it sends is acted on.
    public partial class ServerNetworkHandler
    {
        private enum ClientProtocol { Unconfirmed, Confirmed, Mismatched }

        // Generous on purpose: a slow join must not get a working client flagged as old.
        private const int HandshakeTimeoutMs = 20000;

        private readonly Dictionary<string, ClientProtocol> clientProtocol = new Dictionary<string, ClientProtocol>();

        // One token per join: a timeout left over from an earlier connection of the same player must
        // not judge the new one, which may still be within its own 20 s.
        private readonly Dictionary<string, int> handshakeToken = new Dictionary<string, int>();
        private int nextHandshakeToken;

        // Players already told about a refused admin packet this session, so a panel firing requests
        // every frame does not flood the log.
        private readonly HashSet<string> refusedAdminLogged = new HashSet<string>();

        private string ModVersion => sapi.ModLoader.GetMod("canevents")?.Info?.Version ?? "?";

        private void BeginProtocolCheck(IServerPlayer player)
        {
            string uid = player.PlayerUID;
            clientProtocol[uid] = ClientProtocol.Unconfirmed;
            refusedAdminLogged.Remove(uid);
            int token = ++nextHandshakeToken;
            handshakeToken[uid] = token;

            serverChannel.SendPacket(new ProtocolHandshakeMsg
            {
                ProtocolVersion = CanEventsProtocol.Version,
                ModVersion = ModVersion
            }, player);

            sapi.Event.RegisterCallback(_ =>
            {
                if (!handshakeToken.TryGetValue(uid, out int current) || current != token) return;
                if (!clientProtocol.TryGetValue(uid, out var state) || state != ClientProtocol.Unconfirmed) return;
                if (!(sapi.World.PlayerByUid(uid) is IServerPlayer pl) || pl.ConnectionState != EnumClientState.Playing) return;

                clientProtocol[uid] = ClientProtocol.Mismatched;
                sapi.World.Logger.Warning("[canevents] {0} did not answer the protocol handshake within {1}s - "
                    + "most likely an older client of the mod. Packets from them are ignored until they "
                    + "reconnect with mod {2} (protocol v{3}).",
                    pl.PlayerName, HandshakeTimeoutMs / 1000, ModVersion, CanEventsProtocol.Version);
                pl.SendMessage(GlobalConstants.GeneralChatGroup,
                    Lang.Get("canevents:protocol-no-handshake", ModVersion, CanEventsProtocol.Version),
                    EnumChatType.Notification);
            }, HandshakeTimeoutMs);
        }

        private void OnClientHandshake(IServerPlayer player, ProtocolHandshakeMsg msg)
        {
            if (msg == null) return;

            if (msg.ProtocolVersion == CanEventsProtocol.Version)
            {
                clientProtocol[player.PlayerUID] = ClientProtocol.Confirmed;
                return;
            }

            clientProtocol[player.PlayerUID] = ClientProtocol.Mismatched;
            sapi.World.Logger.Warning("[canevents] {0} runs mod {1} (protocol v{2}); the server runs {3} "
                + "(protocol v{4}). Packets from them are ignored until they reconnect with the server's version.",
                player.PlayerName, msg.ModVersion ?? "?", msg.ProtocolVersion, ModVersion, CanEventsProtocol.Version);
            player.SendMessage(GlobalConstants.GeneralChatGroup,
                Lang.Get("canevents:protocol-mismatch-server", msg.ModVersion ?? "?", msg.ProtocolVersion,
                    ModVersion, CanEventsProtocol.Version),
                EnumChatType.Notification);
        }

        private bool IsProtocolMismatched(IServerPlayer player)
            => clientProtocol.TryGetValue(player.PlayerUID, out var state) && state == ClientProtocol.Mismatched;

        // An unconfirmed client may be an old one whose ids land on the wrong handler, and here the
        // wrong handler edits arenas.
        private bool RequireConfirmedProtocol(IServerPlayer player, Type packetType)
        {
            if (clientProtocol.TryGetValue(player.PlayerUID, out var state) && state == ClientProtocol.Confirmed)
                return true;

            if (refusedAdminLogged.Add(player.PlayerUID))
                sapi.World.Logger.Warning("[canevents] Refused admin packet {0} from {1}: client protocol {2}.",
                    packetType.Name, player.PlayerName,
                    state == ClientProtocol.Mismatched ? "does not match the server" : "not confirmed yet");
            return false;
        }

        private void ForgetClientProtocol(IServerPlayer player)
        {
            if (player == null) return;
            clientProtocol.Remove(player.PlayerUID);
            refusedAdminLogged.Remove(player.PlayerUID);
            handshakeToken.Remove(player.PlayerUID);
        }
    }
}
