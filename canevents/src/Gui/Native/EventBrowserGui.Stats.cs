using System;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace canevents.src.Gui.Native
{
    // Leaderboard and History tabs.
    public partial class EventBrowserGui
    {
        private long expandedHistoryEntry = -1;

        private void ComposeLeaderboardTab()
        {
            var lb = networkHandler.Leaderboard.Value;

            if (lb?.Entries == null || lb.Entries.Length == 0)
            {
                AddCard(Lang.Get("canevents:gui-no-stats"), accent: Palette.Idle);
            }
            else
            {
                // MMR is per-event, only shown on a single-event board.
                bool showMmr = lb.FilteredEventCode != null;

                for (int i = 0; i < lb.Entries.Length; i++)
                {
                    var e = lb.Entries[i];

                    var detail = new StringBuilder();
                    detail.Append(Lang.Get("canevents:gui-record", e.Wins, e.Losses))
                          .Append("   ").Append(Lang.Get("canevents:gui-kd", e.Kills, e.Deaths,
                              e.KDRatio.ToString("F1")))
                          .Append("   ").Append(e.WinRate.ToString("F0")).Append('%');

                    AddCard(Medal(i) + " " + e.PlayerName, detail.ToString(),
                        showMmr ? "MMR " + e.Rating : null,
                        showMmr ? Palette.Info : MedalAccent(i));
                }
            }

            ComposeCards();
            ComposeLeaderboardFilters();
        }

        private void ComposeLeaderboardFilters()
        {
            var lb = networkHandler.Leaderboard.Value;
            double y = ControlsY;
            double x = 0;

            SingleComposer.AddSmallButton(Lang.Get("canevents:gui-filter-all"), () =>
            {
                networkHandler.RequestLeaderboard(null);
                return true;
            }, ButtonAt(x, y), lb?.FilteredEventCode == null ? EnumButtonStyle.Small : EnumButtonStyle.Normal);

            if (lb?.AvailableEventCodes == null) return;

            x += 70;
            foreach (var code in lb.AvailableEventCodes)
            {
                // Rest would run off the dialog.
                if (x > DialogWidth - 120) break;

                string filter = code;
                bool active = lb.FilteredEventCode == code;
                SingleComposer.AddSmallButton(code.ToUpperInvariant(), () =>
                {
                    networkHandler.RequestLeaderboard(filter);
                    return true;
                }, ButtonAt(x, y), active ? EnumButtonStyle.Small : EnumButtonStyle.Normal);
                x += 26 + code.Length * 11;
            }
        }

        private void ComposeHistoryTab()
        {
            var history = networkHandler.MatchHistoryData.Value;

            ComposeTournamentHistory();

            if (history?.Entries == null || history.Entries.Length == 0)
            {
                AddCard(Lang.Get("canevents:gui-no-history"), accent: Palette.Idle);
                ComposeCards();
                ComposeRefreshButton(() =>
                {
                    networkHandler.RequestMatchHistory();
                    networkHandler.RequestTournamentList(includeHistory: true);
                });
                return;
            }

            AddHeader(Lang.Get("canevents:gui-tab-history"));

            foreach (var entry in history.Entries)
            {
                var dt = DateTimeOffset.FromUnixTimeSeconds(entry.TimestampUnix).LocalDateTime;
                string winners = entry.WinnerNames != null && entry.WinnerNames.Length > 0
                    ? string.Join(", ", entry.WinnerNames) : "-";

                var detail = new StringBuilder();
                detail.Append(Lang.Get("canevents:gui-winner", winners));
                if (entry.DurationSeconds > 0)
                    detail.Append("   ").Append(entry.DurationSeconds / 60).Append("m ")
                          .Append(entry.DurationSeconds % 60).Append('s');

                bool expanded = expandedHistoryEntry == entry.TimestampUnix;
                if (expanded && entry.Scores != null)
                {
                    foreach (var s in entry.Scores)
                    {
                        detail.Append('\n').Append(s.PlayerName ?? "")
                              .Append("   ").Append(Lang.Get("canevents:gui-kd", s.Kills, s.Deaths, ""))
                              .Append("   ").Append(Lang.Get(s.Won ? "canevents:gui-win" : "canevents:gui-loss"));
                    }
                }

                long stamp = entry.TimestampUnix;
                AddCard(string.Format("{0}   {1}", entry.EventCode?.ToUpperInvariant(), dt.ToString("dd.MM HH:mm")),
                    detail.ToString(),
                    Lang.Get(expanded ? "canevents:gui-collapse" : "canevents:gui-expand"),
                    expanded ? Palette.Title : Palette.Idle,
                    () =>
                    {
                        expandedHistoryEntry = expanded ? -1 : stamp;
                        ComposeDialog();
                    });
            }

            ComposeCards();
            ComposeRefreshButton(() =>
            {
                networkHandler.RequestMatchHistory();
                networkHandler.RequestTournamentList(includeHistory: true);
            });
        }

        // Circled digits (U+2460..) are missing from the game font.
        private static string Medal(int rank) => (rank + 1) + ".";

        private static double[] MedalAccent(int rank) => rank switch
        {
            0 => Palette.Title,
            1 => Palette.Header,
            2 => Palette.Live,
            _ => Palette.Idle
        };
    }
}
