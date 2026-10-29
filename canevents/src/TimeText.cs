using System;

namespace canevents.src
{
    // Shared by server chat and client HUD so both format the same way.
    public static class TimeText
    {
        // Coarse: "3d 4h", "2h 10m", "45m", "20s".
        public static string Duration(long seconds)
        {
            if (seconds < 0) seconds = 0;
            if (seconds < 60) return seconds + "s";
            if (seconds < 3600) return (seconds / 60) + "m";
            if (seconds < 86400)
            {
                long h = seconds / 3600, m = (seconds % 3600) / 60;
                return m == 0 ? h + "h" : h + "h " + m + "m";
            }
            long d = seconds / 86400, hours = (seconds % 86400) / 3600;
            return hours == 0 ? d + "d" : d + "d " + hours + "h";
        }

        // "4:32" or "45s".
        public static string Short(float seconds)
        {
            int s = Math.Max(0, (int)Math.Round(seconds));
            return s < 60 ? s + "s" : (s / 60) + ":" + (s % 60).ToString("00");
        }
    }
}
