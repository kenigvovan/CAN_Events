using Vintagestory.API.Server;

namespace canevents.src.Network
{
    // Capture the Flag place setup: the flag posts, one per team. Like the other per-behavior setup
    // handlers, the place is addressed by id (unlike /canep ctfpost, which re-derives it from the
    // caller's position and picks the wrong one of two overlapping arenas) and the coordinates come
    // from the admin's feet. The edit itself lives in PlaceConfigService, shared with that command.
    public partial class ServerNetworkHandler
    {
        // CTF has no setup packet of its own, so these are the bare PlaceEdit flow.
        private void OnSetCtfPost(IServerPlayer player, SetCtfPostMsg msg)
            => PlaceEdit(player, msg.PlaceId, p => PlaceConfig.SetCtfPost(p, FeetBlock(player), msg.Team));

        private void OnClearCtfPosts(IServerPlayer player, ClearCtfPostsMsg msg)
            => PlaceEdit(player, msg.PlaceId, p => PlaceConfig.ClearCtfPosts(p));
    }
}
