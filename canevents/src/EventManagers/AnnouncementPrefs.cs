using Vintagestory.API.Server;

namespace canevents.src.EventManagers
{
    // A player's "quiet mode": no server-wide event announcements and no signup popups. Kept in ModData.
    // Only general announcements are muted; the player's own queue and match messages still arrive.
    public static class AnnouncementPrefs
    {
        private const string QuietKey = "canevents:quietannouncements";

        public static bool IsQuiet(IServerPlayer player)
        {
            var data = player?.GetModdata(QuietKey);
            return data != null && data.Length > 0 && data[0] != 0;
        }

        public static void SetQuiet(IServerPlayer player, bool quiet)
        {
            if (player == null) return;
            if (quiet) player.SetModdata(QuietKey, new byte[] { 1 });
            else player.RemoveModdata(QuietKey);
        }
    }
}
