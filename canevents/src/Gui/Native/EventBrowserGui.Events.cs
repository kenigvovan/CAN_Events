using System.Collections.Generic;
using System.Linq;
using System.Text;
using canevents.src.EventManagers;
using canevents.src.Network;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace canevents.src.Gui.Native
{
    // Ranked / Casual tabs: joinable queues, matches already running, and the last match's result.
    public partial class EventBrowserGui
    {
        private void ComposeEventsTab(bool rated)
        {
            var eventList = networkHandler.EventList.Value;

            var queued = new List<EventListEntryMsg>();
            var running = new List<EventListEntryMsg>();
            if (eventList?.Events != null)
            {
                foreach (var ev in eventList.Events)
                {
                    if (ev.Rated != rated) continue;
                    if (ev.EventId == 0) queued.Add(ev); else running.Add(ev);
                }
            }

            if (queued.Count == 0 && running.Count == 0)
            {
                AddCard(Lang.Get(rated ? "canevents:gui-no-ranked" : "canevents:gui-no-casual"),
                    accent: Palette.Idle);
            }
            else
            {
                // An open registration window is on a clock, so it goes to the top of the list where
                // it is read first. OrderBy, not List.Sort: the rest must keep the order the server
                // sent, and Sort is not stable.
                var ordered = queued.OrderByDescending(e => networkHandler.HasOpenSignup(e.GameModeCode));

                // No heading above the queues: the tab itself is the heading.
                foreach (var ev in ordered) AddEventCard(ev);
                if (running.Count > 0)
                {
                    AddHeader(Lang.Get("canevents:gui-section-running"));
                    foreach (var ev in running) AddEventCard(ev);
                }
            }

            AddLastResultCard();
            ComposeCards();
            ComposeRefreshButton(() => networkHandler.RequestEventList());
            ComposeSignupNotice();
        }

        private void AddEventCard(EventListEntryMsg ev)
        {
            string title = ev.GameModeCode?.ToUpperInvariant() ?? "?";
            string badge;
            double[] accent;
            System.Action onClick = null;

            if (ev.IsQueued)
            {
                badge = Lang.Get("canevents:gui-leave-queue");
                accent = Palette.Stop;
                onClick = () =>
                {
                    networkHandler.SendLeaveRequest(ev.GameModeCode);
                    networkHandler.RequestEventList();
                };
            }
            else if (ev.EventId == 0)
            {
                // More than one loadout set: the pick dialog sends the join itself.
                bool hasLoadouts = ev.LoadoutSets != null && ev.LoadoutSets.Length > 1;
                badge = Lang.Get(hasLoadouts ? "canevents:gui-choose-class" : "canevents:gui-join");
                accent = ev.MinPlayers > 0 && ev.CurrentPlayers >= ev.MinPlayers ? Palette.Go : Palette.Title;
                onClick = () =>
                {
                    if (hasLoadouts) OpenLoadoutPick(ev);
                    else
                    {
                        networkHandler.SendJoinRequest(ev.GameModeCode);
                        networkHandler.RequestEventList();
                    }
                };
            }
            else
            {
                badge = StatusText(ev.State);
                accent = ev.State == EventState.Running ? Palette.Live : Palette.Idle;
            }

            // An event taking sign-ups right now is the one thing on this list with a deadline, so it
            // carries the live colour whatever its lobby count says.
            if (ev.EventId == 0 && !ev.IsQueued && networkHandler.HasOpenSignup(ev.GameModeCode))
                accent = Palette.Live;

            // The fill bar only means something for a lobby; a running match's headcount is alive players.
            double? fill = ev.EventId == 0 && ev.MaxPlayers > 0
                ? (double?)ev.CurrentPlayers / ev.MaxPlayers
                : null;

            AddCard(title, BuildEventDetail(ev), badge, accent, onClick, fill, RewardIcons(ev));
        }

        // What you can win, as icons. The fee stays as text so paid and won items are not confused.
        private List<ItemStack> RewardIcons(EventListEntryMsg ev)
        {
            if (ev.EventId != 0) return null;

            var icons = new List<ItemStack>();
            if (ev.PlacementRewards != null && ev.PlacementRewards.Length > 0)
                icons.AddRange(GuiItemUtil.ResolvePacked(capi, ev.PlacementRewards[0].Items));
            else
                icons.AddRange(GuiItemUtil.ResolvePacked(capi, ev.WinnerItems));

            icons.AddRange(GuiItemUtil.ResolvePacked(capi, ev.ParticipationItems));
            return icons.Count > 0 ? icons : null;
        }

        // All of it inline: the native list has no hover popups to hide details in.
        private string BuildEventDetail(EventListEntryMsg ev)
        {
            var sb = new StringBuilder();

            // That registration is open is said here; the seconds are not. A card is drawn into a
            // texture, so a countdown baked into it would sit frozen until the next list packet - the
            // live clock is the dynamic line under the list (see ComposeSignupNotice).
            if (ev.EventId == 0 && networkHandler.HasOpenSignup(ev.GameModeCode))
                sb.Append(Lang.Get("canevents:gui-signup-open")).Append('\n');

            sb.Append(ev.CurrentPlayers).Append(" / ").Append(ev.MaxPlayers);

            if (ev.EventId == 0 && ev.MinPlayers > 0)
            {
                int needed = ev.MinPlayers - ev.CurrentPlayers;
                sb.Append("   ").Append(needed > 0
                    ? Lang.Get("canevents:gui-need-more", needed)
                    : Lang.Get("canevents:gui-ready"));
            }
            else sb.Append("   ").Append(StatusText(ev.State));

            if (ev.EventId != 0 && !string.IsNullOrEmpty(ev.ArenaName))
                sb.Append("   @ ").Append(ev.ArenaName);

            if (ev.EventId == 0 && !string.IsNullOrEmpty(ev.EntryFeeItems))
                sb.Append('\n').Append(Lang.Get("canevents:gui-fee")).Append(": ")
                  .Append(GuiItemUtil.DescribePacked(capi, ev.EntryFeeItems));

            string prizes = DescribePrizes(ev);
            if (prizes != null) sb.Append('\n').Append(prizes);

            return sb.ToString();
        }

        // Only what the icon row cannot say: that the icons ARE prizes, and the personal reward cap.
        private string DescribePrizes(EventListEntryMsg ev)
        {
            if (ev.EventId != 0) return null;

            var sb = new StringBuilder();
            bool hasPrizes = (ev.PlacementRewards != null && ev.PlacementRewards.Length > 0)
                || !string.IsNullOrEmpty(ev.WinnerItems)
                || !string.IsNullOrEmpty(ev.ParticipationItems);
            if (hasPrizes) sb.Append(Lang.Get("canevents:gui-prizes")).Append(':');

            if (ev.RewardCapActive)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(ev.RewardsRemaining > 0
                    ? Lang.Get("canevents:gui-rewards-left", ev.RewardsRemaining)
                    : Lang.Get("canevents:gui-rewards-capped"));
                if (ev.SecondsToNextReward > 0)
                    sb.Append("   ").Append(Lang.Get("canevents:gui-rewards-next",
                        GuiItemUtil.FormatDuration(ev.SecondsToNextReward)));
            }

            return sb.Length > 0 ? sb.ToString() : null;
        }

        private void AddLastResultCard()
        {
            var result = networkHandler.LastResult;
            if (result == null) return;

            AddHeader(Lang.Get("canevents:gui-section-last-match"));

            string winners = result.WinnerNames != null && result.WinnerNames.Length > 0
                ? string.Join(", ", result.WinnerNames)
                : "-";

            var sb = new StringBuilder();
            sb.Append(Lang.Get("canevents:gui-duration")).Append(' ')
              .Append((int)(result.DurationSeconds / 60)).Append(':')
              .Append(((int)(result.DurationSeconds % 60)).ToString("D2"));

            if (result.FinalScores != null)
            {
                foreach (var score in result.FinalScores)
                {
                    sb.Append('\n').Append(score.PlayerName).Append("   ")
                      .Append(score.Kills).Append(' ').Append(Lang.Get("canevents:gui-kills"));
                    if (score.Alive) sb.Append("   ").Append(Lang.Get("canevents:gui-win"));
                }
            }

            AddCard(Lang.Get("canevents:gui-winner", winners), sb.ToString(), accent: Palette.Go);
        }

        // The browser steps aside for the pick: both grab the mouse.
        private void OpenLoadoutPick(EventListEntryMsg ev)
        {
            if (LoadoutPick == null)
            {
                networkHandler.SendJoinRequest(ev.GameModeCode);   // no picker: join with the default set
                networkHandler.RequestEventList();
                return;
            }
            TryClose();
            LoadoutPick.OpenForQueue(ev);
        }

        // Guards against undefined values: the server may be a newer build with unknown states.
        private static string StatusText(EventState state)
        {
            if (!System.Enum.IsDefined(typeof(EventState), state)) return Lang.Get("canevents:gui-state-queue");
            return state switch
            {
                EventState.SearchingForLocation => Lang.Get("canevents:gui-state-waiting"),
                EventState.LocationFound        => Lang.Get("canevents:gui-state-starting"),
                EventState.Running              => Lang.Get("canevents:gui-state-running"),
                EventState.Finished             => Lang.Get("canevents:gui-state-finished"),
                _                               => Lang.Get("canevents:gui-state-queue")
            };
        }
    }
}
