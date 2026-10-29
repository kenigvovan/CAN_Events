using System;
using System.Collections.Generic;
using canevents.src.Gui.Admin.GuiElements;
using canevents.src.Gui.Admin.Widgets;
using canevents.src.Network;
using canevents.src.Tournaments;
using Newtonsoft.Json;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace canevents.src.Gui.Admin.Pages
{
    // Editor is split into panes because the fixed-size window cannot scroll one long form.
    public sealed partial class TournamentsPage : AdminPageBase
    {
        private const double LineHeight = 18;
        private const double FieldHeight = 26;
        private const double LeftWidth = 320;

        private const int PaneSettings = 0;
        private const int PaneEvents = 1;
        private const int PanePayouts = 2;

        private static readonly string[] PaneKeys =
            { "canevents:admin-tour-pane-settings", "canevents:admin-tour-pane-events", "canevents:admin-tour-pane-payouts" };

        private static readonly string[] StateKeys =
            { "draft", "signup", "seeded", "running", "finished", "cancelled" };

        private readonly TournamentEditorBuffer tour = new TournamentEditorBuffer();
        private int pane = PaneSettings;

        protected override PanelTab Tab => PanelTab.Tournaments;

        protected override string[] SubTabLangKeys => new[]
            { "canevents:admin-tour-sub-setup", "canevents:admin-tour-sub-bracket" };

        public override void Watch(PageWatchList watch)
        {
            watch.Add(Net.TournamentList).Add(Net.TournamentBracket)
                 .Add(Net.AdminEventList).Add(Net.PlaceList).Add(Net.EventList);
        }

        public override void OnShown()
        {
            // Bracket snapshot is shared with the player browser and Debug tab; may hold another tournament.
            tour.BracketRequested = false;
        }

        protected override void OnSubTabChanged(int subTab)
        {
            if (subTab == 1 && tour.SelectedId > 0)
            {
                Net.RequestTournamentBracket(tour.SelectedId);
                tour.BracketRequested = true;
            }
        }

        protected override void BuildSubTab(PageBuildContext ctx, int subTab)
        {
            var list = Net.TournamentList.Value;

            // Selected tournament may have been deleted by chat or another admin.
            if (list != null && tour.SelectedId > 0 && !TournamentEditorBuffer.ListContains(list, tour.SelectedId))
                tour.Select(0);

            if (tour.SelectedId > 0) tour.EnsureLoaded(Net);

            if (subTab == 1) BuildBracket(ctx);
            else BuildSetup(ctx, list);
        }

        private void BuildSetup(PageBuildContext ctx, TournamentListMsg list)
        {
            double top = ctx.Current.fixedY;
            var left = ctx.Column(0, LeftWidth);
            var right = ctx.Column(LeftWidth + Card.ColumnGap, ctx.Width - LeftWidth - Card.ColumnGap);

            double controlHeight = BuildControlCard(ctx, left, list, measureOnly: true, y: 0);
            double controlY = ctx.Bottom - controlHeight;
            BuildListCard(ctx, left, top, controlY - Card.Gap - top, list);
            BuildControlCard(ctx, left, list, measureOnly: false, y: controlY);

            BuildEditor(ctx, right, top, list);
        }

        private void BuildListCard(PageBuildContext ctx, ElementBounds column, double y, double height,
                                   TournamentListMsg list)
        {
            var compo = ctx.Compo;
            ElementBounds inner = Card.Frame(compo, column, y, height, Lang.Get("canevents:admin-tour-list-title"));
            Card.AddRefresh(ctx, column, y, "tour-list-refresh", () => Net.RequestTournamentList());

            var rowAnchor = inner.FlatCopy().WithFixedHeight(0);
            var buttons = new ButtonRow(compo, rowAnchor, inner.fixedWidth, 0, "tour-list-btn");
            buttons.Add(Lang.Get("canevents:admin-tour-new"), () =>
            {
                tour.ResetForNewDraft();
                pane = PaneSettings;
                ClearDraftsWithPrefix("tour-");
                Rebuild();
            }, Lang.Get("canevents:admin-tour-new-tip"));

            double tableY = inner.fixedY + ButtonRow.ButtonExtent + 4;

            if (list == null || list.Count == 0)
            {
                var b = inner.FlatCopy().WithFixedHeight(LineHeight);
                b.fixedY = tableY;
                compo.AddStaticText(Lang.Get("canevents:admin-tour-list-empty"), AdminFonts.Hint, b, "tour-list-empty");
                return;
            }

            var spec = new ColumnSpec(
                new ColumnDef(Lang.Get("canevents:admin-tour-col-name"), 0),
                new ColumnDef(Lang.Get("canevents:admin-tour-col-state"), 64),
                new ColumnDef(Lang.Get("canevents:admin-tour-col-entrants"), 50, EnumTextOrientation.Right));

            // Use the accessors: protobuf sends an all-default array as null.
            var rows = new List<ColumnRow>(list.Count);
            for (int i = 0; i < list.Count; i++)
            {
                long id = list.Ids[i];
                int state = list.StateAt(i);
                var row = new ColumnRow("#" + id + " " + list.NameAt(i), StateLabel(state),
                        list.EntrantsAt(i) + "/" + list.MaxEntrantsAt(i))
                    .WithColor(1, StateColor(state))
                    .WithColor(2, AdminColors.Label);
                string events = list.EventsAt(i).Replace(";", ", ");
                if (events.Length > 0) row.WithCellTooltip(0, Lang.Get("canevents:admin-tour-row-events", events));
                row.Selected = id == tour.SelectedId && !tour.Creating;
                row.OnClick = () =>
                {
                    if (id == tour.SelectedId && !tour.Creating) return;
                    // Drops an unsaved draft.
                    tour.Creating = false;
                    tour.Select(id);
                    ClearDraftsWithPrefix("tour-");
                    Rebuild();
                };
                rows.Add(row);
            }

            var anchor = ElementBounds.Fixed(inner.fixedX, tableY, inner.fixedWidth, 0);
            double listTop = tableY + ColumnTable.HeaderHeight + 4;
            double listHeight = Math.Max(40, inner.fixedY + inner.fixedHeight - listTop - 6);
            ColumnTable.Add(ctx, anchor, spec, rows, listHeight, new ScrollableListOptions { Key = "tour-list" });
        }

        // Measured first so the list above can take the remaining height.
        private double BuildControlCard(PageBuildContext ctx, ElementBounds column, TournamentListMsg list,
                                        bool measureOnly, double y)
        {
            double innerWidth = column.fixedWidth - Card.Padding * 2;
            string title = Lang.Get("canevents:admin-tour-control-title");

            if (tour.SelectedId <= 0 || tour.Creating)
            {
                string hint = Lang.Get(tour.Creating ? "canevents:admin-tour-control-save-first" : "canevents:admin-tour-control-none");
                double hintHeight = ButtonRow.TextHeight(hint, innerWidth);
                double h = Card.HeightAround(hintHeight);
                if (measureOnly) return h;

                ElementBounds hintInner = Card.Frame(ctx.Compo, column, y, h, title);
                ctx.Compo.AddStaticText(hint, AdminFonts.Hint, hintInner.FlatCopy().WithFixedHeight(hintHeight), "tour-control-hint");
                return h;
            }

            long id = tour.SelectedId;
            var state = (TournamentState)TournamentEditorBuffer.StateOf(list, id);
            var actions = ControlActions(state, id);

            string[] captions = new string[actions.Count];
            for (int i = 0; i < actions.Count; i++) captions[i] = actions[i].caption;

            string note = state == TournamentState.Running ? Lang.Get("canevents:admin-tour-running-note")
                        : state >= TournamentState.Finished ? Lang.Get("canevents:admin-tour-over-note")
                        : null;
            double noteHeight = note != null ? ButtonRow.TextHeight(note, innerWidth) : 0;
            double buttonsHeight = actions.Count > 0 ? ButtonRow.HeightFor(innerWidth, 0, captions) : 0;
            double content = LineHeight + 6 + buttonsHeight + noteHeight;
            double height = Card.HeightAround(content);
            if (measureOnly) return height;

            var compo = ctx.Compo;
            ElementBounds inner = Card.Frame(compo, column, y, height, title);

            string entrants = "";
            if (list != null)
            {
                for (int i = 0; i < list.Count; i++)
                    if (list.Ids[i] == id) entrants = list.EntrantsAt(i) + "/" + list.MaxEntrantsAt(i);
            }

            var stateLine = inner.FlatCopy().WithFixedHeight(LineHeight);
            var stateFont = CairoFont.WhiteSmallText().WithColor(StateColor((int)state));
            compo.AddStaticText(Card.Fit(Lang.Get("canevents:admin-tour-state-line", StateLabel((int)state), entrants),
                stateFont, innerWidth), stateFont, stateLine, "tour-state-line");

            double rowY = inner.fixedY + LineHeight + 6;
            if (actions.Count > 0)
            {
                var anchor = inner.FlatCopy().WithFixedHeight(0);
                anchor.fixedY = rowY;
                var row = new ButtonRow(compo, anchor, innerWidth, 0, "tour-ctl");
                foreach (var a in actions) row.Add(a.caption, a.onClick, a.tooltip);
                rowY += buttonsHeight;
            }

            if (note != null)
            {
                var b = inner.FlatCopy().WithFixedHeight(noteHeight);
                b.fixedY = rowY;
                compo.AddStaticText(note, AdminFonts.Hint, b, "tour-control-note");
            }
            return height;
        }

        private List<(string caption, Action onClick, string tooltip)> ControlActions(TournamentState state, long id)
        {
            var result = new List<(string, Action, string)>();
            switch (state)
            {
                case TournamentState.Draft:
                    result.Add((Lang.Get("canevents:admin-tour-open-signup"),
                        () => SendTour(TourAdminAction.OpenSignup, id, Lang.Get("canevents:admin-tour-log-opensignup", id)),
                        Lang.Get("canevents:admin-tour-open-signup-tip")));
                    break;
                case TournamentState.Signup:
                    result.Add((Lang.Get("canevents:admin-tour-close-signup"),
                        () => SendTour(TourAdminAction.CloseSignup, id, Lang.Get("canevents:admin-tour-log-closesignup", id)),
                        Lang.Get("canevents:admin-tour-close-signup-tip")));
                    result.Add((Lang.Get("canevents:admin-tour-begin-now"),
                        () => SendTour(TourAdminAction.Begin, id, Lang.Get("canevents:admin-tour-log-begin", id)),
                        null));
                    break;
                case TournamentState.Seeded:
                    result.Add((Lang.Get("canevents:admin-tour-begin"),
                        () => SendTour(TourAdminAction.Begin, id, Lang.Get("canevents:admin-tour-log-begin", id)),
                        null));
                    break;
                case TournamentState.Running:
                    result.Add((Lang.Get("canevents:admin-tour-call-next"),
                        () => SendTour(TourAdminAction.ForceCall, id, Lang.Get("canevents:admin-tour-log-forcecall", id)),
                        Lang.Get("canevents:admin-tour-call-next-tip")));
                    break;
            }

            if (state <= TournamentState.Running)
            {
                // Opens a side panel that asks how to end it.
                result.Add((Lang.Get("canevents:admin-tour-cancel"), () => Gui.OpenSideDialog(new TourCancelPanel(
                    id,
                    refund => SendTour(TourAdminAction.Cancel, id,
                        Lang.Get(refund ? "canevents:admin-tour-log-cancel-refund" : "canevents:admin-tour-log-cancel-keep", id),
                        boolArg: refund),
                    () =>
                    {
                        SendTour(TourAdminAction.Delete, id, Lang.Get("canevents:admin-tour-log-delete", id));
                        tour.SelectedId = 0;
                        tour.LoadedFor = -1;
                        Rebuild();
                    })), Lang.Get("canevents:admin-tour-cancel-tip")));
            }
            return result;
        }

        private void BuildEditor(PageBuildContext ctx, ElementBounds column, double y, TournamentListMsg list)
        {
            var compo = ctx.Compo;
            double innerWidth = column.fixedWidth - Card.Padding * 2;

            if (tour.SelectedId <= 0 && !tour.Creating)
            {
                string hint = Lang.Get("canevents:admin-tour-editor-none");
                double hintHeight = ButtonRow.TextHeight(hint, innerWidth);
                ElementBounds noneInner = Card.Frame(compo, column, y, Card.HeightAround(hintHeight),
                    Lang.Get("canevents:admin-tour-editor-title"));
                compo.AddStaticText(hint, AdminFonts.Hint, noneInner.FlatCopy().WithFixedHeight(hintHeight), "tour-editor-none");
                return;
            }

            int state = tour.Creating ? (int)TournamentState.Draft : TournamentEditorBuffer.StateOf(list, tour.SelectedId);
            bool editable = state <= (int)TournamentState.Seeded;

            double headerContent = FieldHeight + 8 + ButtonRow.ButtonExtent;
            double headerHeight = Card.HeightAround(headerContent, withTitle: false);
            ElementBounds head = Card.Frame(compo, column, y, headerHeight, null);

            double x = head.fixedX;
            for (int i = 0; i < PaneKeys.Length; i++)
            {
                string caption = Lang.Get(PaneKeys[i]);
                double w = ButtonRow.ButtonWidth(caption) - 6;
                var b = ElementBounds.Fixed(x, head.fixedY, w, FieldHeight);
                int paneIndex = i;
                string key = "tour-pane-" + i;
                compo.AddToggleButton(caption, CairoFont.WhiteSmallText(), on =>
                {
                    pane = paneIndex;
                    Rebuild();
                }, b, key);
                compo.GetToggleButton(key).On = pane == i;
                x += w + 6;
            }

            double rowY = head.fixedY + FieldHeight + 8;
            string saveCaption = Lang.Get("canevents:admin-tour-save");
            var saveBounds = ElementBounds.Fixed(head.fixedX, rowY, ButtonRow.ButtonWidth(saveCaption), ButtonRow.ButtonHeight);
            compo.AddButton(saveCaption, new ActionConsumable(() =>
            {
                Save();
                Rebuild();
                return true;
            }), saveBounds, EnumButtonStyle.Normal, "tour-save");
            if (!editable) compo.GetButton("tour-save").Enabled = false;
            Tooltip.Add(compo, Lang.Get(tour.Creating ? "canevents:admin-tour-save-create-tip" : "canevents:admin-tour-save-tip"),
                saveBounds, "tip-tour-save");

            double textX = saveBounds.fixedX + saveBounds.fixedWidth + 8;
            if (tour.Creating)
            {
                string discard = Lang.Get("canevents:admin-tour-discard");
                string discardArmed = Lang.Get("canevents:admin-tour-discard-armed");
                double dw = ArmedTextButton.Width(discard, discardArmed);
                var discardBounds = ElementBounds.Fixed(textX, rowY, dw, ButtonRow.ButtonHeight);
                ArmedTextButton.Add(ctx, discardBounds, "tour-discard", discard, () =>
                {
                    tour.Creating = false;
                    ClearDraftsWithPrefix("tour-");
                }, discardArmed);
                textX += dw + 10;
            }

            string what = tour.Creating
                ? Lang.Get("canevents:admin-tour-new-unsaved")
                : Lang.Get("canevents:admin-tour-editing", tour.SelectedId, tour.Name);
            double[] whatColor = tour.Creating ? AdminColors.Section : AdminColors.Value;
            if (!editable)
            {
                what = Lang.Get("canevents:admin-tour-locked");
                whatColor = AdminColors.Warning;
            }
            var whatFont = CairoFont.WhiteSmallText().WithColor(whatColor);
            double whatWidth = head.fixedX + head.fixedWidth - textX;
            var whatBounds = ElementBounds.Fixed(textX, rowY + 6, Math.Max(20, whatWidth), LineHeight);
            Card.AddFitLine(compo, what, whatFont, whatBounds, "tour-editing", "tip-tour-editing");

            double paneY = y + headerHeight + Card.Gap;
            double paneHeight = ctx.Bottom - paneY;
            switch (pane)
            {
                case PaneEvents: BuildEventsPane(ctx, column, paneY, paneHeight); break;
                case PanePayouts: BuildPayoutsPane(ctx, column, paneY, paneHeight); break;
                default: BuildSettingsPane(ctx, column, paneY, paneHeight); break;
            }
        }

        private void Save()
        {
            // Nothing selected: create instead of "configure #0".
            if (!tour.Creating && tour.SelectedId <= 0) tour.Creating = true;

            bool creating = tour.Creating;
            long id = tour.SelectedId;
            string json = JsonConvert.SerializeObject(tour.BuildDraft());
            string log = creating
                ? Lang.Get("canevents:admin-tour-log-create", tour.Name)
                : Lang.Get("canevents:admin-tour-log-save", id, tour.Name);

            SendTour(creating ? TourAdminAction.Create : TourAdminAction.Configure, id, log, configJson: json);
            tour.Creating = false;
            tour.ReloadAfterSave(Net);
        }

        private void SendTour(TourAdminAction action, long tournamentId, string log, int matchId = 0,
                              string stringArg = null, bool boolArg = false, string configJson = null)
        {
            Act(log, () =>
            {
                Net.SendTournamentAdminAction(new TournamentAdminActionMsg
                {
                    Action = TournamentEditorBuffer.WireName(action),
                    TournamentId = tournamentId,
                    MatchId = matchId,
                    StringArg = stringArg,
                    BoolArg = boolArg,
                    ConfigJson = configJson
                });
                Net.RequestTournamentList();
            });
        }

        private static string StateLabel(int state)
            => state >= 0 && state < StateKeys.Length
                ? Lang.Get("canevents:admin-debug-tour-state-" + StateKeys[state])
                : "?";

        private static double[] StateColor(int state)
        {
            switch ((TournamentState)state)
            {
                case TournamentState.Running: return AdminColors.Success;
                case TournamentState.Signup: return AdminColors.Section;
                case TournamentState.Cancelled: return AdminColors.Danger;
                case TournamentState.Finished: return AdminColors.Muted;
                default: return AdminColors.Warning;
            }
        }

        private static string Place(int index) => Lang.Get("canevents:admin-tour-place", index + 1);

        // Field keys are reused across tournaments, so stale half-typed text must go.
        private void ClearDraftsWithPrefix(string prefix)
        {
            var stale = new List<string>();
            foreach (var key in State.Drafts.Keys)
                if (key.StartsWith(prefix, StringComparison.Ordinal)) stale.Add(key);
            foreach (var key in stale) State.Drafts.Remove(key);

            if (SettingsTableKey.StartsWith(prefix, StringComparison.Ordinal))
            {
                State.SettingsEditKey.Remove(SettingsTableKey);
                State.SettingsEditValue.Remove(SettingsTableKey);
            }
        }

        private static void Line(PageBuildContext ctx, double x, double y, double width, string key, string text,
                                 double[] color)
        {
            var font = CairoFont.WhiteSmallText().WithColor(color);
            Card.AddFitLine(ctx.Compo, text, font, ElementBounds.Fixed(x, y, width, LineHeight), key, "tip-" + key);
        }
    }
}
