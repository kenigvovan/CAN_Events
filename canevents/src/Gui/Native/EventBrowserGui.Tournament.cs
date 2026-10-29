using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using canevents.src.Network;
using canevents.src.Tournaments;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace canevents.src.Gui.Native
{
    // Tournaments tab: sign-up list and the bracket of the opened one.
    public partial class EventBrowserGui
    {
        private long openBracketId;
        private string teamNameInput = "";
        private int selectedBracketMatch = -1;
        private float bracketScrollX;
        private float bracketScrollY;

        private void ComposeTournamentTab()
        {
            if (openBracketId > 0) { ComposeBracketView(); return; }

            var list = networkHandler.TournamentList.Value;
            if (list == null || list.Count == 0)
            {
                AddCard(Lang.Get("canevents:gui-tournament-none"), accent: Palette.Idle);
                ComposeCards();
                ComposeRefreshButton(() => networkHandler.RequestTournamentList());
                return;
            }

            // Use the accessors: protobuf drops all-default arrays, leaving the raw fields null.
            int shown = 0;
            for (int i = 0; i < list.Count; i++)
            {
                long id = list.Ids[i];
                var state = (TournamentState)list.StateAt(i);

                // Finished ones go to the History tab.
                if (state == TournamentState.Finished || state == TournamentState.Cancelled) continue;
                shown++;
                bool joined = list.JoinedAt(i);
                int entrants = list.EntrantsAt(i);
                int max = list.MaxEntrantsAt(i);
                int teamSize = list.TeamSizeAt(i);

                var detail = new StringBuilder();
                detail.Append(StateWord(state))
                      .Append("   ")
                      .Append(Lang.Get("canevents:gui-tournament-entrants", entrants, max));

                // Otherwise a scheduled draft reads as "draft 0/16".
                long opensAt = list.OpensAt(i);
                if (state == TournamentState.Draft && opensAt > 0)
                    detail.Append("   ").Append(Lang.Get("canevents:gui-tournament-opens-in",
                        GuiItemUtil.FormatDuration(opensAt - UnixTime.Now)));

                string events = list.EventsAt(i).Replace(";", ", ");
                if (events.Length > 0) detail.Append('\n').Append(events.ToUpperInvariant());
                if (teamSize > 1)
                    detail.Append("   ").Append(teamSize).Append('v').Append(teamSize);

                string fee = list.FeeAt(i);
                if (fee.Length > 0)
                    detail.Append('\n').Append(Lang.Get("canevents:gui-tournament-fee",
                        GuiItemUtil.DescribePacked(capi, fee)));
                string pot = list.PotAt(i);
                if (pot.Length > 0)
                    detail.Append('\n').Append(Lang.Get("canevents:gui-tournament-pot",
                        GuiItemUtil.DescribePacked(capi, pot)));

                // Card opens the bracket; join/leave live inside the bracket view.
                bool canJoin = state == TournamentState.Signup && !joined && entrants < max;
                string badge = canJoin
                    ? Lang.Get("canevents:gui-tournament-open-join")
                    : Lang.Get("canevents:gui-tournament-view");
                double[] accent = canJoin ? Palette.Go
                    : state == TournamentState.Running ? Palette.Live
                    : joined ? Palette.Info : Palette.Idle;
                Action onClick = () => OpenBracket(id);

                AddCard(list.NameAt(i), detail.ToString(), badge, accent, onClick,
                    progress: max > 0 ? (double?)entrants / max : null);
            }

            if (shown == 0) AddCard(Lang.Get("canevents:gui-tournament-none"), accent: Palette.Idle);

            ComposeCards();

            SingleComposer.AddSmallButton(Lang.Get("canevents:gui-refresh"),
                () => { networkHandler.RequestTournamentList(); return true; },
                ButtonAt(0, ControlsY), EnumButtonStyle.Normal);
        }

        // Includes archived ones, so history is requested from the server explicitly.
        private void ComposeTournamentHistory()
        {
            // Broadcasts carry active tournaments only.
            var entries = networkHandler.TournamentHistoryEntries();
            if (entries.Count > 0) AddHeader(Lang.Get("canevents:gui-tab-tournaments"));

            foreach (var (list, i) in entries)
            {
                var state = (TournamentState)list.StateAt(i);
                long id = list.Ids[i];
                var detail = new StringBuilder();
                detail.Append(StateWord(state))
                      .Append("   ")
                      .Append(Lang.Get("canevents:gui-tournament-entrants",
                          list.EntrantsAt(i), list.MaxEntrantsAt(i)));

                string events = list.EventsAt(i).Replace(";", ", ");
                if (events.Length > 0) detail.Append('\n').Append(events.ToUpperInvariant());

                AddCard(list.NameAt(i), detail.ToString(),
                    Lang.Get("canevents:gui-tournament-view"),
                    state == TournamentState.Cancelled ? Palette.Stop : Palette.Idle,
                    () => { curTab = 5; OpenBracket(id); });
            }
        }

        private void OpenBracket(long id)
        {
            openBracketId = id;
            selectedBracketMatch = -1;
            bracketScrollX = 0;
            bracketScrollY = 0;
            networkHandler.RequestTournamentBracket(id);
            RecomposeIfOpen();
        }

        private void ComposeBracketView()
        {
            var msg = networkHandler.TournamentBracket.Value;
            if (msg == null || msg.TournamentId != openBracketId)
            {
                AddCard(Lang.Get("canevents:gui-loading"), accent: Palette.Idle);
                ComposeCards();
                ComposeBracketControls();
                return;
            }

            var view = BracketView.FromMsg(msg);
            if (view.Matches.Count == 0)
            {
                // Fewer than two entrants: no tree yet, show the field and schedule.
                AddHeader(view.Name);
                string schedule = ScheduleSummary(msg);
                if (schedule.Length > 0) AddCard(schedule, accent: Palette.Info);
                foreach (var e in view.Entrants.Values.OrderBy(e => e.Name))
                    AddCard(e.Name, accent: Palette.Idle);
                if (view.Entrants.Count == 0)
                    AddCard(Lang.Get("canevents:gui-tournament-empty"), accent: Palette.Idle);
                ComposeCards();
                ComposeBracketControls();
                return;
            }

            double canvasHeight = viewportHeight - DetailStripHeight;

            // Fit to canvas by default; scroll once boxes would go below MinBoxWidth.
            BracketLayout.Layout layout;
            double scale = 1;
            if (bracketZoomed)
                layout = BracketLayout.Compute(view);
            else
                layout = BracketLayout.ComputeFitted(view,
                    DialogWidth - 2 * BracketElement.Margin,
                    canvasHeight - 2 * BracketElement.Margin, 0.45, out scale);

            var element = new BracketElement(capi, view,
                ElementBounds.Fixed(0, ListTop, DialogWidth, canvasHeight), layout, scale);
            element.OnMatchClicked = id => { selectedBracketMatch = id; RecomposeIfOpen(); };

            var viewBounds = ElementBounds.Fixed(0, ListTop, DialogWidth, canvasHeight);
            var innerBounds = ElementBounds.Fixed(-bracketScrollX, ListTop - bracketScrollY,
                Math.Max(DialogWidth, element.ContentWidth),
                Math.Max(canvasHeight, element.ContentHeight));
            element.Bounds = innerBounds;

            // Not copied from viewBounds: the fork rewrites its child's fixed coords.
            var insetBounds = ElementBounds.Fixed(-6, ListTop - 6, DialogWidth + 12, canvasHeight + 12);
            var clip = viewBounds.ForkBoundingParent();
            var vScroll = ElementBounds.Fixed(DialogWidth + 10, ListTop, 12, canvasHeight);

            SingleComposer
                .AddInset(insetBounds, 4)
                .BeginClip(clip)
                .AddInteractiveElement(element, "bracket")
                .EndClip()
                .AddInteractiveElement(new SlimScrollbar(capi, OnBracketScrollY, vScroll), "bracketscrolly");

            bracketElement = element;
            bracketCanvasHeight = canvasHeight;
            bracketPanMax = Math.Max(0, element.ContentWidth - DialogWidth);
            bracketScrollMax = Math.Max(0, element.ContentHeight - canvasHeight);

            ComposeBracketDetail(view, ListTop + canvasHeight + 4);
            ComposeBracketControls();
        }

        // Match detail panel under the canvas (heading + body).
        private const double DetailStripHeight = 46;
        private bool bracketZoomed;
        private double bracketCanvasHeight;

        private BracketElement bracketElement;
        private double bracketPanMax;
        private double bracketScrollMax;

        // Heights are only final after Compose. See UpdateScrollbar.
        private void UpdateBracketScrollbar()
        {
            var scrollbar = SingleComposer?.GetScrollbar("bracketscrolly");
            if (scrollbar == null || bracketElement == null) return;

            float wanted = bracketScrollY;
            scrollbar.Bounds.CalcWorldBounds();
            scrollbar.SetHeights((float)bracketCanvasHeight, (float)bracketElement.ContentHeight);

            wanted = (float)Math.Min(wanted, bracketScrollMax);
            bracketScrollY = wanted;
            scrollbar.CurrentYPosition = wanted;
            OnBracketScrollY(wanted);

            if (scrollbar is SlimScrollbar slim) slim.Visible = bracketScrollMax > 0.5;
        }

        private void OnBracketScrollY(float value)
        {
            bracketScrollY = value;
            if (bracketElement == null) return;
            bracketElement.Bounds.fixedY = ListTop - value;
            bracketElement.Bounds.CalcWorldBounds();
        }

        // Buttons: the native composer has no horizontal scrollbar.
        private void PanBracket(double delta)
        {
            bracketScrollX = (float)Math.Clamp(bracketScrollX + delta, 0, bracketPanMax);
            if (bracketElement == null) return;
            bracketElement.Bounds.fixedX = -bracketScrollX;
            bracketElement.Bounds.CalcWorldBounds();
        }

        private void ComposeBracketDetail(BracketView view, double y)
        {
            var m = view.Matches.FirstOrDefault(x => x.Id == selectedBracketMatch);

            string heading, body, status = null;
            double[] statusColor = Palette.Idle;

            if (view.IsPreview)
            {
                heading = Lang.Get("canevents:gui-tournament-preview");
                body = view.Entrants.Count == 0
                    ? Lang.Get("canevents:gui-tournament-empty")
                    : string.Join(", ", view.Entrants.Values.OrderBy(e => e.Name).Select(e => e.Name));
                status = view.Entrants.Count.ToString();
                statusColor = Palette.Info;
            }
            else if (m == null)
            {
                heading = view.Name;
                body = view.FinalPlacement.Count > 0
                    ? string.Join("   ", view.FinalPlacement.Take(3)
                        .Select((id, i) => (i + 1) + ". " + view.NameOf(id)))
                    : Lang.Get("canevents:gui-tournament-pick-match");
                if (!string.IsNullOrEmpty(view.PotPacked))
                {
                    status = GuiItemUtil.DescribePacked(capi, view.PotPacked);
                    statusColor = Palette.Title;
                }
            }
            else
            {
                heading = BracketLayout.RoundLabel(view, m);
                if (!string.IsNullOrEmpty(m.EventCode))
                    heading += "   " + m.EventCode.ToUpperInvariant();

                body = string.Join("  vs  ", m.Slots.Select(id =>
                {
                    if (string.IsNullOrEmpty(id)) return Lang.Get("canevents:gui-tournament-tbd");
                    string name = view.NameOf(id) ?? "?";
                    string roster = view.MembersOf(id);
                    if (!string.IsNullOrEmpty(roster) && roster != name) name += " (" + roster + ")";
                    if (m.IsAbsent(id)) name += " " + Lang.Get("canevents:gui-tournament-noshow");
                    return name;
                }));

                (status, statusColor) = MatchStatus(view, m);
            }

            var strip = ElementBounds.Fixed(0, y, DialogWidth, DetailStripHeight - 4);
            SingleComposer.AddInset(strip.FlatCopy().FixedGrow(4), 3);

            SingleComposer.AddStaticText(Ellipsize(heading, 44),
                CairoFont.WhiteSmallText().WithColor(Palette.Title),
                ElementBounds.Fixed(6, y + 1, DialogWidth * 0.55, 18));

            SingleComposer.AddStaticText(Ellipsize(body, 76),
                CairoFont.WhiteDetailText(),
                ElementBounds.Fixed(6, y + 19, DialogWidth - 12, 18));

            if (!string.IsNullOrEmpty(status))
            {
                SingleComposer.AddStaticText(Ellipsize(status, 26),
                    CairoFont.WhiteSmallText().WithColor(statusColor)
                        .WithOrientation(EnumTextOrientation.Right),
                    ElementBounds.Fixed(DialogWidth * 0.55, y + 1, DialogWidth * 0.45 - 6, 18));
            }
        }

        private (string text, double[] color) MatchStatus(BracketView view, BracketView.Match m)
        {
            string key = BracketPalette.StatusKey(m.State);
            var color = BracketPalette.ColorOf(m.State);

            switch ((BracketMatchState)m.State)
            {
                case BracketMatchState.AwaitingReady:
                    return (Lang.Get(key, Math.Max(0, m.ReadyDeadlineUnix - UnixTime.Now)), color);
                case BracketMatchState.Done:
                    return (Lang.Get(key, view.NameOf(m.Winner) ?? "?"), Palette.Title);
                default:
                    return (Lang.Get(key), color);
            }
        }

        private static string Ellipsize(string text, int max)
            => string.IsNullOrEmpty(text) || text.Length <= max ? text : text.Substring(0, max - 3) + "...";

        private void ComposeBracketControls()
        {
            SingleComposer.AddSmallButton(Lang.Get("canevents:gui-back"),
                () => { openBracketId = 0; bracketElement = null; RecomposeIfOpen(); return true; },
                ButtonAt(0, ControlsY), EnumButtonStyle.Normal);

            SingleComposer.AddSmallButton(Lang.Get("canevents:gui-refresh"),
                () => { networkHandler.RequestTournamentBracket(openBracketId); return true; },
                ButtonAt(80, ControlsY), EnumButtonStyle.Normal);

            long id = openBracketId;
            int index = IndexOf(id);
            if (index >= 0)
            {
                var list = networkHandler.TournamentList.Value;
                var state = (TournamentState)list.StateAt(index);
                bool joined = list.JoinedAt(index);
                bool canJoin = state == TournamentState.Signup && !joined
                            && list.EntrantsAt(index) < list.MaxEntrantsAt(index);
                bool canLeave = joined && (state == TournamentState.Signup || state == TournamentState.Seeded);

                // Without a team name a side shows as "Leader +4".
                bool teamBracket = list.TeamSizeAt(index) > 1;
                double buttonX = 170;
                if (teamBracket && (canJoin || canLeave))
                {
                    SingleComposer.AddTextInput(
                        ElementBounds.Fixed(170, ControlsY - 2, 150, 24),
                        text => teamNameInput = text, CairoFont.WhiteDetailText(), "teamname");
                    SingleComposer.GetTextInput("teamname").SetPlaceHolderText(
                        Lang.Get("canevents:gui-tournament-teamname"));
                    if (!string.IsNullOrEmpty(teamNameInput))
                        SingleComposer.GetTextInput("teamname").SetValue(teamNameInput);
                    buttonX = 330;
                }

                // No re-request: BroadcastTournamentUpdate pushes list and bracket on accept.
                if (canJoin)
                    SingleComposer.AddSmallButton(Lang.Get("canevents:gui-tournament-signup"),
                        () => { networkHandler.SendTournamentSignup(id, true, teamNameInput); return true; },
                        ButtonAt(buttonX, ControlsY), EnumButtonStyle.Small);
                else if (canLeave)
                {
                    if (teamBracket)
                        SingleComposer.AddSmallButton(Lang.Get("canevents:gui-tournament-rename"),
                            () => { networkHandler.SendTournamentTeamName(id, teamNameInput); return true; },
                            ButtonAt(buttonX, ControlsY), EnumButtonStyle.Small);

                    SingleComposer.AddSmallButton(Lang.Get("canevents:gui-tournament-withdraw"),
                        () => { networkHandler.SendTournamentSignup(id, false); return true; },
                        ButtonAt(teamBracket ? buttonX + 90 : buttonX, ControlsY), EnumButtonStyle.Normal);
                }

                // One class pick for the whole bracket, via the queue's dialog.
                if (canLeave && LoadoutPick != null)
                {
                    var ev = FindEventEntry(list.EventsAt(index));
                    if (ev?.LoadoutSets != null && ev.LoadoutSets.Length > 1)
                    {
                        var allowed = networkHandler.TournamentBracket.Value?.TournamentId == id
                            ? (networkHandler.TournamentBracket.Value.AllowedLoadouts ?? new string[0]).ToList()
                            : new List<string>();
                        SingleComposer.AddSmallButton(Lang.Get("canevents:gui-choose-class"),
                            () => { LoadoutPick.OpenForTournament(ev, id, allowed); return true; },
                            ButtonAt(teamBracket ? buttonX + 180 : buttonX + 90, ControlsY),
                            EnumButtonStyle.Normal);
                    }
                }
            }

            if (bracketZoomed || bracketPanMax > 0 || bracketScrollMax > 0)
            {
                SingleComposer.AddSmallButton(
                    Lang.Get(bracketZoomed ? "canevents:gui-tournament-fit" : "canevents:gui-tournament-zoom"),
                    () =>
                    {
                        bracketZoomed = !bracketZoomed;
                        bracketScrollX = 0;
                        bracketScrollY = 0;
                        RecomposeIfOpen();
                        return true;
                    },
                    ButtonAt(DialogWidth - 200, ControlsY), EnumButtonStyle.Normal);
            }

            if (bracketPanMax > 0)
            {
                SingleComposer.AddSmallButton("<", () => { PanBracket(-160); return true; },
                    ButtonAt(DialogWidth - 90, ControlsY), EnumButtonStyle.Normal);
                SingleComposer.AddSmallButton(">", () => { PanBracket(160); return true; },
                    ButtonAt(DialogWidth - 45, ControlsY), EnumButtonStyle.Normal);
            }
        }

        // Entry for the tournament's first mode, for its class list.
        private EventListEntryMsg FindEventEntry(string packedCodes)
        {
            var events = networkHandler.EventList.Value?.Events;
            if (events == null || string.IsNullOrEmpty(packedCodes)) return null;
            string first = packedCodes.Split(';')[0];
            return events.FirstOrDefault(e =>
                string.Equals(e.GameModeCode, first, StringComparison.OrdinalIgnoreCase));
        }

        private int IndexOf(long tournamentId)
        {
            var list = networkHandler.TournamentList.Value;
            if (list == null) return -1;
            for (int i = 0; i < list.Count; i++)
                if (list.Ids[i] == tournamentId) return i;
            return -1;
        }

        // Empty for a one-off opened by hand.
        private static string ScheduleSummary(TournamentBracketMsg msg)
        {
            var parts = new List<string>();
            if (msg.OpenAtUnix > 0 && msg.State == (int)TournamentState.Draft)
                parts.Add(Lang.Get("canevents:gui-tournament-opens-in",
                    GuiItemUtil.FormatDuration(msg.OpenAtUnix - UnixTime.Now)));
            if (msg.SignupWindowMinutes > 0)
                parts.Add(Lang.Get("canevents:gui-tournament-signup-window", msg.SignupWindowMinutes));
            if (!string.IsNullOrEmpty(msg.RepeatAtPacked))
                parts.Add(Lang.Get("canevents:gui-tournament-repeats-at",
                    msg.RepeatAtPacked.Replace(";", ", ")));
            else if (msg.RepeatEveryMinutes > 0)
                parts.Add(Lang.Get("canevents:gui-tournament-repeats-every",
                    GuiItemUtil.FormatDuration(msg.RepeatEveryMinutes * 60)));
            return string.Join("   ", parts);
        }

        private static string StateWord(TournamentState state)
        {
            switch (state)
            {
                case TournamentState.Signup: return Lang.Get("canevents:gui-tournament-state-signup");
                case TournamentState.Seeded: return Lang.Get("canevents:gui-tournament-state-seeded");
                case TournamentState.Running: return Lang.Get("canevents:gui-tournament-state-running");
                case TournamentState.Finished: return Lang.Get("canevents:gui-tournament-state-finished");
                case TournamentState.Cancelled: return Lang.Get("canevents:gui-tournament-state-cancelled");
                default: return Lang.Get("canevents:gui-tournament-state-draft");
            }
        }
    }
}
