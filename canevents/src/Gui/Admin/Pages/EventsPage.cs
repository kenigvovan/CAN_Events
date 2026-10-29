using System;
using System.Collections.Generic;
using canevents.src.Gui.Admin.GuiElements;
using canevents.src.Gui.Admin.Widgets;
using canevents.src.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace canevents.src.Gui.Admin.Pages
{
    // Events tab: every event, whether it could start now, and open / start / switch-off buttons.
    public sealed partial class EventsPage : CANGuiPage
    {
        private const double FieldLabelWidth = 170;
        private const double FieldWidth = 70;
        private const double RowHeight = 26;

        // Minutes "Open" keeps registration open. -1 = server default, 0 = until "Start now".
        private int signupMinutes = -1;

        // Client ms; open windows' countdowns tick locally from it between packets.
        private long scheduleReceivedMs;

        private Action onScheduleChanged;
        private long tickListenerId;

        private bool builtWithCountdown;

        // Lifecycle

        public override void Watch(PageWatchList watch)
        {
            watch.Add(Net.AdminEventList).Add(Net.ScheduleState).Add(Net.EventSettings);
        }

        public override void OnShown()
        {
            scheduleReceivedMs = Capi.ElapsedMilliseconds;
            onScheduleChanged = () => scheduleReceivedMs = Capi.ElapsedMilliseconds;
            Net.ScheduleState.Changed += onScheduleChanged;

            // The countdown in the Ready column has to move; the table is static once composed, so it
            // is rebuilt once a second - only while some window actually has a deadline.
            // The last build that showed a countdown also earns one more rebuild, so a window that
            // just ran out leaves the table instead of freezing at its last second.
            tickListenerId = Capi.Event.RegisterGameTickListener(dt =>
            {
                if (builtWithCountdown) { Rebuild(); return; }
                foreach (var window in OpenSignups())
                {
                    if (window.left >= 0f) { Rebuild(); return; }
                }
            }, 1000);
        }

        public override void OnHidden()
        {
            if (onScheduleChanged != null) Net.ScheduleState.Changed -= onScheduleChanged;
            onScheduleChanged = null;
            if (tickListenerId != 0) Capi.Event.UnregisterGameTickListener(tickListenerId);
            tickListenerId = 0;
        }

        // Negative left = no deadline. Expired windows are left out: the server already started that
        // match, and a Cancel would only answer "no window is open".
        private List<(string code, float left)> OpenSignups()
        {
            var result = new List<(string, float)>();
            var state = Net.ScheduleState.Value;
            var codes = state?.OpenSignupCodes;
            if (codes == null) return result;

            float elapsed = Math.Max(0, Capi.ElapsedMilliseconds - scheduleReceivedMs) / 1000f;
            for (int i = 0; i < codes.Length; i++)
            {
                if (string.IsNullOrEmpty(codes[i])) continue;
                // Protobuf omits an all-default array, so the seconds can arrive short or missing;
                // -1 is also the real value for a window with no deadline.
                float left = state.OpenSignupSeconds != null && i < state.OpenSignupSeconds.Length
                    ? state.OpenSignupSeconds[i] : -1f;
                if (left >= 0f)
                {
                    left -= elapsed;
                    if (left <= 0f) continue;
                }
                result.Add((codes[i], left));
            }
            return result;
        }

        // Building

        protected override void BuildContent(PageBuildContext ctx)
        {
            builtWithCountdown = false;
            var compo = ctx.Compo;
            var column = ctx.Column(0, ctx.Width);
            double y = ctx.Current.fixedY + 4;

            string hint = Lang.Get("canevents:admin-events-hint");
            double innerWidth = ctx.Width - Card.Padding * 2;
            double hintHeight = ButtonRow.TextHeight(hint, innerWidth);
            double cardHeight = Card.HeightAround(hintHeight + 6 + RowHeight);

            ElementBounds inner = Card.Frame(compo, column, y, cardHeight, Lang.Get("canevents:admin-events-title"));
            Card.AddRefresh(ctx, column, y, "ev-refresh", () => Net.RequestAdminEventList());

            compo.AddStaticText(hint, AdminFonts.Hint, inner.FlatCopy().WithFixedHeight(hintHeight), "ev-hint");

            // Signup window: caption, field, and what the two special values mean.
            double rowY = inner.fixedY + hintHeight + 6;
            var label = inner.FlatCopy().WithFixedSize(FieldLabelWidth, 20);
            label.fixedY = rowY + 4;
            compo.AddStaticText(Lang.Get("canevents:admin-events-signup-label"), AdminFonts.Body, label, "ev-signup-label");

            var field = inner.FlatCopy().WithFixedSize(FieldWidth, RowHeight);
            field.fixedX += FieldLabelWidth;
            field.fixedY = rowY;
            NumberField.AddInt(ctx, field, "ev-signup-min", () => signupMinutes, v => signupMinutes = v,
                -1, 1440, Lang.Get("canevents:admin-events-signup-help"));

            var explain = inner.FlatCopy().WithFixedSize(innerWidth - FieldLabelWidth - FieldWidth - 10, 20);
            explain.fixedX += FieldLabelWidth + FieldWidth + 10;
            explain.fixedY = rowY + 4;
            compo.AddStaticText(Lang.Get("canevents:admin-events-signup-explain"), AdminFonts.Hint, explain, "ev-signup-explain");

            y += cardHeight + Card.Gap;

            if (settingsCode != null)
            {
                BuildSettings(ctx, y);
                return;
            }

            var events = Net.AdminEventList.Value?.Events;
            if (events == null || events.Length == 0)
            {
                var empty = ElementBounds.Fixed(ctx.Main.fixedX, y, ctx.Width, 22);
                compo.AddStaticText(Lang.Get("canevents:admin-events-empty"), AdminFonts.Hint, empty, "ev-empty");
                return;
            }

            BuildTable(ctx, events, y);
        }

        private void BuildTable(PageBuildContext ctx, AdminEventListEntryMsg[] events, double y)
        {
            var spec = new ColumnSpec(
                new ColumnDef(Lang.Get("canevents:admin-events-col-code"), 0),
                new ColumnDef(Lang.Get("canevents:admin-events-col-category"), 90),
                new ColumnDef(Lang.Get("canevents:admin-events-col-players"), 60),
                new ColumnDef(Lang.Get("canevents:admin-events-col-ready"), 170),
                new ColumnDef(Lang.Get("canevents:admin-events-col-rotation"), 70),
                new ColumnDef(null, 350));

            var signups = OpenSignups();
            builtWithCountdown = signups.Exists(w => w.left >= 0f);
            var rows = new List<ColumnRow>(events.Length);
            foreach (var e in events) rows.Add(BuildRow(ctx, e, signups));

            var anchor = ElementBounds.Fixed(ctx.Main.fixedX, y, ctx.Width, 0);
            double listTop = y + ColumnTable.HeaderHeight + 4;
            double listHeight = Math.Max(60, ctx.Bottom - listTop - 10);

            ColumnTable.Add(ctx, anchor, spec, rows, listHeight, new ScrollableListOptions { Key = "ev-table" });
        }

        private ColumnRow BuildRow(PageBuildContext ctx, AdminEventListEntryMsg e, List<(string code, float left)> signups)
        {
            int openIdx = signups.FindIndex(w => w.code == e.Code);
            bool signupOpen = openIdx >= 0;

            string players = e.MaxPlayers > 0 ? e.MinPlayers + "-" + e.MaxPlayers : "-";

            // While registration is open, the time left is what the admin is watching for;
            // readiness was already checked when the window opened.
            string readyText;
            double[] readyColor;
            string readyHelp;
            if (signupOpen)
            {
                float left = signups[openIdx].left;
                readyText = left < 0f
                    ? Lang.Get("canevents:admin-events-signup-manual")
                    : Lang.Get("canevents:admin-events-signup-countdown", TimeText.Short(left));
                readyColor = AdminColors.Section;
                readyHelp = null;
            }
            else
            {
                Readiness(e, out readyText, out readyColor, out readyHelp);
            }

            var row = new ColumnRow(e.Code ?? "", e.Category ?? "", players, readyText, null, null)
                .WithColor(0, e.Enabled ? AdminColors.Value : AdminColors.Muted)
                .WithColor(1, AdminColors.Muted)
                .WithColor(2, AdminColors.Muted)
                .WithColor(3, readyColor);

            if (e.EventPlaceTypes != null && e.EventPlaceTypes.Length > 0)
            {
                string tip = Lang.Get("canevents:admin-events-places-tip", string.Join(" + ", e.EventPlaceTypes), e.RequiredSpawns);
                if (e.Rated) tip += "\n" + Lang.Get("canevents:admin-events-rated-tip");
                row.WithCellTooltip(0, tip);
            }
            if (readyHelp != null) row.WithCellTooltip(3, readyHelp);

            AddRowButtons(ctx, row, e, signupOpen);
            return row;
        }

        private void AddRowButtons(PageBuildContext ctx, ColumnRow row, AdminEventListEntryMsg e, bool signupOpen)
        {
            const int col = 5;
            bool canRun = e.Enabled && e.Readiness == EventReadiness.Ready;
            string code = e.Code;

            // Rotation: what the auto-scheduler announces in turn. Live on the server, no restart.
            var rotation = Net.ScheduleState.Value?.RotationCodes;
            bool inRotation = rotation != null
                && Array.Exists(rotation, c => string.Equals(c, code, StringComparison.OrdinalIgnoreCase));
            row.WithButton(4, inRotation ? "[x]" : "[ ]", () =>
            {
                Act(Lang.Get(inRotation ? "canevents:admin-events-log-rotation-off" : "canevents:admin-events-log-rotation-on", code),
                    () => Net.SendAdminAction(inRotation ? AdminAction.RotationRemove : AdminAction.RotationAdd, code));
            }, Lang.Get(inRotation ? "canevents:admin-events-rotation-off-tip" : "canevents:admin-events-rotation-on-tip"));

            // The same button position is the way in and the way out, so a window opened by mistake
            // is closed where it was opened rather than on another tab.
            if (signupOpen)
            {
                // Everyone who joined the window is dropped, so it asks first.
                ArmedTextButton.AddToRow(ctx, row, col, "ev-cancel-" + code,
                    Lang.Get("canevents:admin-events-cancel"), Lang.Get("canevents:admin-events-cancel-armed"),
                    Lang.Get("canevents:admin-events-cancel-tip"), () => PanelActions.CancelSignup(Gui, code));
            }
            else
            {
                // The length is read at click time: the field may have been typed in since this build.
                row.WithButton(col, Lang.Get("canevents:admin-events-open"), () => PanelActions.OpenSignup(Gui, code, signupMinutes),
                    Lang.Get("canevents:admin-events-open-tip"), enabled: canRun);
            }

            row.WithButton(col, Lang.Get("canevents:admin-events-start-now"), () => PanelActions.ForceStart(Gui, code),
                Lang.Get("canevents:admin-events-start-now-tip"), enabled: canRun);

            row.WithButton(col, Lang.Get("canevents:admin-events-settings"), () => OpenSettings(code),
                Lang.Get("canevents:admin-events-settings-tip"));

            bool enabled = e.Enabled;
            row.WithButton(col,
                Lang.Get(enabled ? "canevents:admin-events-disable" : "canevents:admin-events-enable"),
                () => Act(Lang.Get(enabled ? "canevents:admin-events-log-disable" : "canevents:admin-events-log-enable", code),
                    () => Net.SendToggleEventEnabled(code)),
                Lang.Get(enabled ? "canevents:admin-events-disable-tip" : "canevents:admin-events-enable-tip"));
        }

        internal static void Readiness(AdminEventListEntryMsg e, out string label, out double[] color, out string help)
        {
            string detail = e.ReadinessDetail ?? "?";
            switch (e.Readiness)
            {
                case EventReadiness.Ready:
                    color = AdminColors.Success;
                    label = Lang.Get("canevents:admin-events-ready");
                    help = Lang.Get("canevents:admin-events-ready-tip", detail);
                    break;
                case EventReadiness.AllBusy:
                    color = AdminColors.Warning;
                    label = Lang.Get("canevents:admin-events-busy");
                    help = Lang.Get("canevents:admin-events-busy-tip", detail);
                    break;
                case EventReadiness.NoSpawns:
                    color = AdminColors.Danger;
                    label = Lang.Get("canevents:admin-events-nospawns");
                    help = Lang.Get("canevents:admin-events-nospawns-tip", detail);
                    break;
                case EventReadiness.IncompleteSetup:
                    color = AdminColors.Danger;
                    label = Lang.Get("canevents:admin-events-incomplete");
                    help = Lang.Get("canevents:admin-events-incomplete-tip", detail);
                    break;
                case EventReadiness.AllDisabled:
                    color = AdminColors.Danger;
                    label = Lang.Get("canevents:admin-events-arenaoff");
                    help = Lang.Get("canevents:admin-events-arenaoff-tip", detail);
                    break;
                case EventReadiness.EventDisabled:
                    color = AdminColors.Muted;
                    label = Lang.Get("canevents:admin-events-eventoff");
                    help = Lang.Get("canevents:admin-events-eventoff-tip");
                    break;
                default:
                    color = AdminColors.Danger;
                    label = Lang.Get("canevents:admin-events-noarena");
                    help = Lang.Get("canevents:admin-events-noarena-tip", detail);
                    break;
            }
        }
    }
}
