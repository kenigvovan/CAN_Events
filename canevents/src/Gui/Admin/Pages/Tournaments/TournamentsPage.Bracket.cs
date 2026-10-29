using System;
using System.Linq;
using canevents.src.Gui.Admin.Widgets;
using canevents.src.Gui.Native;
using canevents.src.Tournaments;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace canevents.src.Gui.Admin.Pages
{
    public sealed partial class TournamentsPage
    {
        private const double ToolbarHeight = 30;
        private const double DetailHeight = 46;
        private const double ScrollbarWidth = 20;

        // Below this the boxes stop shrinking and the canvas scrolls.
        private const double MinBracketScale = 0.55;

        private int selectedMatch = -1;
        private long selectedMatchOf;
        private bool bracketZoomed;
        private float bracketScrollY;
        private double bracketScrollX;

        private void BuildBracket(PageBuildContext ctx)
        {
            var compo = ctx.Compo;
            double top = ctx.Current.fixedY;
            double width = ctx.Width;

            if (tour.Creating || tour.SelectedId <= 0)
            {
                Line(ctx, 0, top + 10, width, "tour-br-none",
                    Lang.Get(tour.Creating ? "canevents:admin-tour-bracket-unsaved" : "canevents:admin-tour-bracket-none"),
                    AdminColors.Muted);
                return;
            }

            long id = tour.SelectedId;
            if (selectedMatchOf != id)
            {
                selectedMatchOf = id;
                selectedMatch = -1;
                bracketScrollX = 0;
                bracketScrollY = 0;
            }

            var msg = Net.TournamentBracket.Value;
            if (msg == null || msg.TournamentId != id)
            {
                if (!tour.BracketRequested)
                {
                    Net.RequestTournamentBracket(id);
                    tour.BracketRequested = true;
                }
                AddSmall(ctx, 0, top, "tour-br-refresh", Lang.Get("canevents:admin-debug-refresh"), () => Net.RequestTournamentBracket(id));
                Line(ctx, 90, top + 4, width - 90, "tour-br-loading", Lang.Get("canevents:admin-tour-bracket-loading"), AdminColors.Muted);
                return;
            }

            var view = BracketView.FromMsg(msg);

            double x = AddSmall(ctx, 0, top, "tour-br-refresh", Lang.Get("canevents:admin-debug-refresh"),
                () => Net.RequestTournamentBracket(id));

            if (view.Matches.Count == 0)
            {
                double y0 = top + ToolbarHeight;
                Line(ctx, 0, y0, width, "tour-br-few", Lang.Get("canevents:admin-tour-bracket-too-few"), AdminColors.Muted);
                if (view.Entrants.Count > 0)
                {
                    string names = string.Join(", ", view.Entrants.Values.OrderBy(e => e.Name).Select(e => e.Name));
                    double h = ButtonRow.TextHeight(names, width);
                    compo.AddStaticText(Lang.Get("canevents:admin-tour-bracket-entrants", view.Entrants.Count, names),
                        AdminFonts.Body, ElementBounds.Fixed(0, y0 + LineHeight + 4, width, h + LineHeight), "tour-br-entrants");
                }
                return;
            }

            string zoomCaption = Lang.Get(bracketZoomed ? "canevents:admin-tour-bracket-fit" : "canevents:admin-tour-bracket-full");
            x = AddSmall(ctx, x, top, "tour-br-zoom", zoomCaption, () =>
            {
                bracketZoomed = !bracketZoomed;
                bracketScrollX = 0;
                bracketScrollY = 0;
                Rebuild();
            });

            int footerLines = (view.IsPreview ? 2 : 0) + (view.FinalPlacement.Count > 0 ? 1 : 0);
            double canvasTop = top + ToolbarHeight + 4;
            double canvasWidth = width - ScrollbarWidth - 8;
            double canvasHeight = ctx.Bottom - canvasTop - DetailHeight - 10 - footerLines * LineHeight;

            BracketLayout.Layout layout;
            double scale = 1;
            if (bracketZoomed)
                layout = BracketLayout.Compute(view);
            else
                layout = BracketLayout.ComputeFitted(view, canvasWidth - 2 * BracketElement.Margin,
                    canvasHeight - 2 * BracketElement.Margin, MinBracketScale, out scale);

            // Element is sized to the whole tree and scrolled under a clip, like the player browser.
            // Same as BracketElement.ContentWidth/Height, needed before the element exists.
            double contentWidth = Math.Max(canvasWidth, layout.Width + 2 * BracketElement.Margin);
            double contentHeight = Math.Max(canvasHeight, layout.Height + 2 * BracketElement.Margin);

            double panMax = Math.Max(0, contentWidth - canvasWidth);
            float scrollMax = (float)Math.Max(0, contentHeight - canvasHeight);
            bracketScrollX = Math.Clamp(bracketScrollX, 0, panMax);
            bracketScrollY = Math.Clamp(bracketScrollY, 0, scrollMax);

            var elementBounds = ElementBounds.Fixed(-bracketScrollX, -bracketScrollY, contentWidth, contentHeight);
            var element = new BracketElement(Capi, view, elementBounds, layout, scale);
            element.OnMatchClicked = matchId =>
            {
                selectedMatch = matchId;
                Rebuild();
            };
            element.OnMatchRightClicked = matchId => OpenMatchMenu(view, matchId);

            if (panMax > 0)
            {
                // VS composer has no horizontal scrollbar.
                double step = canvasWidth * 0.5;
                x = AddSmall(ctx, x, top, "tour-br-left", "<", () => Pan(element, -step, panMax));
                x = AddSmall(ctx, x, top, "tour-br-right", ">", () => Pan(element, step, panMax));
            }

            string hint = view.IsPreview
                ? Lang.Get("canevents:admin-tour-bracket-preview")
                : Lang.Get("canevents:admin-tour-bracket-hint");
            Line(ctx, x + 6, top + 4, width - x - 6, "tour-br-hint", hint,
                view.IsPreview ? AdminColors.Warning : AdminColors.Muted);

            var clip = ElementBounds.Fixed(0, canvasTop, canvasWidth, canvasHeight);
            compo.AddInset(ElementBounds.Fixed(-3, canvasTop - 3, canvasWidth + 6, canvasHeight + 6), 3);
            compo.BeginClip(clip)
                 .AddInteractiveElement(element, "tour-bracket")
                 .EndClip();

            var scrollBounds = ElementBounds.Fixed(canvasWidth + 8, canvasTop, ScrollbarWidth, canvasHeight);
            compo.AddVerticalScrollbar(value =>
            {
                bracketScrollY = value;
                element.Bounds.fixedY = -value;
                element.Bounds.CalcWorldBounds();
            }, scrollBounds, "tour-bracket-scroll");

            float restoreTo = bracketScrollY;
            ctx.AfterCompose(() =>
            {
                var scrollbar = compo.GetScrollbar("tour-bracket-scroll");
                if (scrollbar == null) return;
                scrollbar.SetHeights((float)canvasHeight, (float)contentHeight);
                // Must follow SetHeights, otherwise the position is clamped against a zero total.
                if (restoreTo <= 0) return;
                scrollbar.CurrentYPosition = Math.Min(restoreTo, scrollMax);
                scrollbar.TriggerChanged();
            });

            double y = canvasTop + canvasHeight + 8;
            BuildMatchDetail(ctx, view, y, width);
            y += DetailHeight + 2;

            if (view.IsPreview)
            {
                string names = view.Entrants.Count == 0
                    ? Lang.Get("canevents:admin-tour-bracket-nobody")
                    : string.Join(", ", view.Entrants.Values.OrderBy(e => e.Name).Select(e => e.Name));
                Line(ctx, 0, y, width, "tour-br-signed", Lang.Get("canevents:admin-tour-bracket-signed", view.Entrants.Count, names),
                    AdminColors.Value);
                y += LineHeight;
                Line(ctx, 0, y, width, "tour-br-signed-hint", Lang.Get("canevents:admin-tour-bracket-signed-hint"), AdminColors.Muted);
                y += LineHeight;
            }

            if (view.FinalPlacement.Count > 0)
            {
                string standings = string.Join(", ", view.FinalPlacement.Select((e, i) => (i + 1) + ". " + view.NameOf(e)));
                Line(ctx, 0, y, width, "tour-br-final", Lang.Get("canevents:admin-tour-bracket-final", standings), AdminColors.Section);
            }
        }

        private void Pan(BracketElement element, double delta, double panMax)
        {
            bracketScrollX = Math.Clamp(bracketScrollX + delta, 0, panMax);
            element.Bounds.fixedX = -bracketScrollX;
            element.Bounds.CalcWorldBounds();
        }

        private void BuildMatchDetail(PageBuildContext ctx, BracketView view, double y, double width)
        {
            var compo = ctx.Compo;
            var m = view.Matches.FirstOrDefault(x => x.Id == selectedMatch);

            compo.AddInset(ElementBounds.Fixed(-3, y - 3, width + 6, DetailHeight + 2), 3);

            if (m == null)
            {
                Line(ctx, 6, y + 2, width - 12, "tour-br-detail-head", view.Name ?? "", AdminColors.Section);
                Line(ctx, 6, y + 22, width - 12, "tour-br-detail-body", Lang.Get("canevents:admin-tour-bracket-pick"), AdminColors.Muted);
                return;
            }

            string heading = Lang.Get("canevents:admin-tour-match-heading", BracketLayout.RoundLabel(view, m), m.Id);
            if (!string.IsNullOrEmpty(m.EventCode)) heading += "   " + Lang.Get("canevents:admin-tour-match-mode", m.EventCode);
            if (!string.IsNullOrEmpty(m.ResultKind)) heading += "   " + Lang.Get("canevents:admin-tour-match-result", m.ResultKind);

            string body = string.Join("  vs  ", m.Slots.Select(side => SideText(view, m, side)));

            var (status, statusColor) = MatchStatus(view, m);
            double statusWidth = 220;
            Line(ctx, 6, y + 2, width - statusWidth - 18, "tour-br-detail-head", heading, AdminColors.Section);
            var statusFont = CairoFont.WhiteSmallText().WithColor(statusColor).WithOrientation(EnumTextOrientation.Right);
            compo.AddStaticText(Card.Fit(status, statusFont, statusWidth), statusFont,
                ElementBounds.Fixed(width - statusWidth - 6, y + 2, statusWidth, LineHeight), "tour-br-detail-status");
            Line(ctx, 6, y + 22, width - 12, "tour-br-detail-body", body, AdminColors.Value);
        }

        private static string SideText(BracketView view, BracketView.Match m, string entrantId)
        {
            if (string.IsNullOrEmpty(entrantId)) return Lang.Get("canevents:gui-tournament-tbd");
            int seed = view.SeedOf(entrantId);
            string name = view.NameOf(entrantId) ?? "?";
            string roster = view.MembersOf(entrantId);
            if (!string.IsNullOrEmpty(roster) && roster != name) name += " (" + roster + ")";
            if (m.IsAbsent(entrantId)) name += " " + Lang.Get("canevents:gui-tournament-noshow");
            return (seed > 0 ? "[" + seed + "] " : "") + name;
        }

        // Same wording and colours as the player browser.
        private static (string text, double[] color) MatchStatus(BracketView view, BracketView.Match m)
        {
            string key = BracketPalette.StatusKey(m.State);
            var color = BracketPalette.ColorOf(m.State);
            switch ((BracketMatchState)m.State)
            {
                case BracketMatchState.AwaitingReady:
                    return (Lang.Get(key, Math.Max(0, m.ReadyDeadlineUnix - UnixTime.Now)), color);
                case BracketMatchState.Done:
                    return (Lang.Get(key, view.NameOf(m.Winner) ?? "?"), color);
                default:
                    return (Lang.Get(key), color);
            }
        }

        // Preview matches do not exist on the server yet, so no menu for them.
        private void OpenMatchMenu(BracketView view, int matchId)
        {
            selectedMatch = matchId;
            var m = view.Matches.FirstOrDefault(x => x.Id == matchId);
            if (m == null) return;

            if (view.IsPreview)
            {
                Gui.SetStatus(false, Lang.Get("canevents:admin-tour-bracket-preview-menu"));
                Rebuild();
                return;
            }

            long id = tour.SelectedId;
            var sides = m.Filled.Select(s => (id: s, name: view.NameOf(s) ?? s)).ToList();
            Gui.OpenSideDialog(new TourMatchPanel(
                Lang.Get("canevents:admin-tour-match-menu-title", matchId),
                BracketLayout.RoundLabel(view, m),
                sides,
                side => SendTour(TourAdminAction.Result, id,
                    Lang.Get("canevents:admin-tour-log-result", id, matchId, view.NameOf(side) ?? side),
                    matchId: matchId, stringArg: side),
                side => SendTour(TourAdminAction.Disqualify, id,
                    Lang.Get("canevents:admin-tour-log-dq", id, view.NameOf(side) ?? side),
                    stringArg: side)));
            Rebuild();
        }

        // Returns the x for the next button.
        private static double AddSmall(PageBuildContext ctx, double x, double y, string key, string caption, Action onClick)
        {
            double w = ButtonRow.ButtonWidth(caption) - 10;
            var b = ElementBounds.Fixed(x, y + 2, w, SmallButtonHeight);
            ctx.Compo.AddSmallButton(caption, new ActionConsumable(() => { onClick(); return true; }),
                b, EnumButtonStyle.Small, key);
            return x + w + 6;
        }
    }
}
