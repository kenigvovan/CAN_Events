using System;

namespace canevents.src
{
    /// <summary>
    /// Countdown strings shared by both sides: the server writes them into chat, the client draws the
    /// same shape on the HUD, and a signup window announced as "5:00" must not read as "5m 00s" ten
    /// seconds later on the panel.
    ///
    /// Distinct from <c>GuiItemUtil.FormatShortDuration</c> on purpose: that one is the admin panel's
    /// "3m 20s" phrasing and is client-only.
    /// </summary>
    public static class TimeText
    {
        /// <summary>"4:32" over a minute, "45s" under it. Negative is clamped to zero.</summary>
        public static string Short(float seconds)
        {
            int s = Math.Max(0, (int)Math.Round(seconds));
            return s < 60 ? s + "s" : (s / 60) + ":" + (s % 60).ToString("00");
        }
    }
}
