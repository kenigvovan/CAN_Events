using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using canevents.src.Gui.Admin.Widgets;
using canevents.src.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace canevents.src.Gui.Admin.Pages
{
    // Countdowns tick locally between scheduler packets and update dynamic texts in place, no rebuild.
    public sealed class DashboardPage : AdminPageBase
    {
        private const int SubOverview = 0, SubServer = 1;
        private const string ServerTableKey = "dash-srv";
        private const double ServerTableWidth = 640;

        private const double LineHeight = 20;
        private const double SignupRowHeight = 32;
        // The button bevel hangs below ButtonExtent, text right under the row touches it.
        private const double RotationGap = 10;
        private const int TickMs = 250;
        private const int LogLinesShown = 8;

        protected override PanelTab Tab => PanelTab.Dashboard;

        protected override string[] SubTabLangKeys => new[]
        {
            "canevents:admin-dash-subtab-overview",
            "canevents:admin-dash-subtab-server"
        };

        private static readonly int[] SchedulerIntervals = { 5, 10, 15, 30, 60 };

        private float secondsUntilNext = -1f;

        // left < 0 means no deadline.
        private readonly List<(string code, float left)> signups = new List<(string, float)>();

        private long tickListenerId;
        private long lastTickMs;
        private Action onScheduleChanged;

        private GuiComposer liveCompo;
        private readonly List<(string key, Func<string> text)> liveTexts = new List<(string, Func<string>)>();
        private readonly Dictionary<string, string> liveShown = new Dictionary<string, string>();

        private string builtLogStamp;

        public override void Watch(PageWatchList watch)
        {
            watch.Add(Net.ScheduleState);
            watch.Add(Net.EventList);
            watch.Add(Net.ServerSettings);
        }

        protected override void OnSubTabChanged(int subTab)
        {
            if (subTab == SubServer) Net.SendRequestServerSettings();
            else Net.RequestScheduleState();
        }

        public override void OnShown()
        {
            if (SubTab == SubServer) Net.SendRequestServerSettings();

            StopTicking();
            Seed();

            onScheduleChanged = Seed;
            Net.ScheduleState.Changed += onScheduleChanged;

            lastTickMs = Capi.ElapsedMilliseconds;
            tickListenerId = Capi.Event.RegisterGameTickListener(OnTick, TickMs);
        }

        public override void OnHidden()
        {
            StopTicking();
            liveCompo = null;
            liveTexts.Clear();
        }

        private void StopTicking()
        {
            if (onScheduleChanged != null)
            {
                Net.ScheduleState.Changed -= onScheduleChanged;
                onScheduleChanged = null;
            }
            if (tickListenerId != 0)
            {
                Capi.Event.UnregisterGameTickListener(tickListenerId);
                tickListenerId = 0;
            }
        }

        private void Seed()
        {
            var state = Net.ScheduleState.Value;
            secondsUntilNext = state?.SecondsUntilNext ?? -1f;

            signups.Clear();
            var codes = state?.OpenSignupCodes;
            if (codes == null) return;
            for (int i = 0; i < codes.Length; i++)
            {
                if (string.IsNullOrEmpty(codes[i])) continue;
                // Protobuf drops all-default arrays, so this can be short or null.
                float left = state.OpenSignupSeconds != null && i < state.OpenSignupSeconds.Length
                    ? state.OpenSignupSeconds[i] : -1f;
                signups.Add((codes[i], left));
            }
        }

        private void OnTick(float _)
        {
            long now = Capi.ElapsedMilliseconds;
            float delta = (now - lastTickMs) / 1000f;
            lastTickMs = now;

            if (secondsUntilNext > 0) secondsUntilNext -= delta;

            bool removed = false;
            for (int i = signups.Count - 1; i >= 0; i--)
            {
                var w = signups[i];
                if (w.left < 0f) continue;             // no deadline
                float left = w.left - delta;
                // Server has already started the match at zero, Cancel would fail.
                if (left <= 0f)
                {
                    signups.RemoveAt(i);
                    removed = true;
                }
                else signups[i] = (w.code, left);
            }

            if (SubTab != SubOverview) return;

            if (removed || LogStamp() != builtLogStamp)
            {
                Rebuild();
                return;
            }
            UpdateLiveTexts();
        }

        private void UpdateLiveTexts()
        {
            if (liveCompo == null) return;
            foreach (var (key, text) in liveTexts)
            {
                string value = text();
                if (liveShown.TryGetValue(key, out string shown) && shown == value) continue;

                var element = liveCompo.GetDynamicText(key);
                if (element == null) continue;
                element.SetNewText(value);
                liveShown[key] = value;
            }
        }

        private void AddLiveText(GuiComposer compo, ElementBounds bounds, string key, CairoFont font, Func<string> text)
        {
            string value = text();
            compo.AddDynamicText(value, font, bounds, key);
            liveTexts.Add((key, text));
            liveShown[key] = value;
        }

        private string LogStamp()
        {
            var log = Gui.ActionLog;
            if (log.Count == 0) return "";
            var last = log[log.Count - 1];
            return log.Count + "|" + last.time + "|" + last.text;
        }

        protected override void BuildSubTab(PageBuildContext ctx, int subTab)
        {
            liveTexts.Clear();
            liveShown.Clear();
            if (subTab == SubServer)
            {
                liveCompo = null;
                BuildServerSettings(ctx);
                return;
            }

            liveCompo = ctx.Compo;
            builtLogStamp = LogStamp();

            var full = ctx.Column(0, ctx.Width);
            double y = ctx.Current.fixedY;
            y = BuildSchedulerCard(ctx, full, y);

            double half = (ctx.Width - Card.ColumnGap) / 2;
            double bottomHeight = Math.Max(150, ctx.Bottom - y);
            BuildRunningCard(ctx, ctx.Column(0, half), y, bottomHeight);
            BuildLogCard(ctx, ctx.Column(half + Card.ColumnGap, half), y, bottomHeight);
        }

        private double BuildSchedulerCard(PageBuildContext ctx, ElementBounds column, double y)
        {
            var compo = ctx.Compo;
            var s = Net.ScheduleState.Value;
            double innerWidth = column.fixedWidth - Card.Padding * 2;

            string rotation = s?.RotationCodes != null && s.RotationCodes.Length > 0
                ? Lang.Get("canevents:admin-dash-rotation", string.Join(" > ", s.RotationCodes))
                : null;

            double contentHeight;
            if (s == null) contentHeight = LineHeight;
            else
            {
                contentHeight = LineHeight * (s.Enabled ? 3 : 2) + 6 + ButtonRow.ButtonExtent;
                if (rotation != null) contentHeight += RotationGap + ButtonRow.TextHeight(rotation, innerWidth);
                if (signups.Count > 0) contentHeight += 8 + LineHeight + signups.Count * SignupRowHeight;
            }

            double height = Card.HeightAround(contentHeight);
            ElementBounds inner = Card.Frame(compo, column, y, height, Lang.Get("canevents:admin-dash-scheduler-title"));
            AddRefreshButton(compo, column, y);

            double lineY = inner.fixedY;
            if (s == null)
            {
                AddText(compo, inner, 0, lineY, innerWidth, Lang.Get("canevents:admin-dash-waiting"), AdminColors.Muted, "dash-waiting");
                return y + height + Card.Gap;
            }

            if (s.Enabled)
            {
                string on = Lang.Get("canevents:admin-dash-on");
                double onWidth = AddText(compo, inner, 0, lineY, innerWidth, on, AdminColors.Success, "dash-onoff") + 8;

                var nextBounds = inner.FlatCopy().WithFixedSize(innerWidth - onWidth, LineHeight);
                nextBounds.fixedX += onWidth;
                nextBounds.fixedY = lineY;
                int interval = s.IntervalMinutes;
                AddLiveText(compo, nextBounds, "dash-next", AdminFonts.Body,
                    () => Lang.Get("canevents:admin-dash-every", interval, FormatDuration(secondsUntilNext)));
                lineY += LineHeight;

                string next = string.IsNullOrEmpty(s.NextEventCode) ? Lang.Get("canevents:admin-dash-next-none") : s.NextEventCode;
                AddText(compo, inner, 0, lineY, innerWidth, Lang.Get("canevents:admin-dash-next-rotation", next),
                    AdminColors.Label, "dash-next-rotation");
                lineY += LineHeight;
            }
            else
            {
                string off = Lang.Get("canevents:admin-dash-off");
                double offWidth = AddText(compo, inner, 0, lineY, innerWidth, off, AdminColors.Muted, "dash-onoff") + 8;
                AddText(compo, inner, offWidth, lineY, innerWidth - offWidth, Lang.Get("canevents:admin-dash-off-hint"),
                    AdminColors.Muted, "dash-off-hint");
                lineY += LineHeight;
            }

            // Scheduler skips its turn unless both hold.
            bool enoughPlayers = s.OnlinePlayers >= s.MinPlayersToStart;
            bool roomToRun = s.RunningEvents < s.MaxConcurrentEvents;
            double playersWidth = AddText(compo, inner, 0, lineY, innerWidth,
                Lang.Get("canevents:admin-dash-players", s.OnlinePlayers, s.MinPlayersToStart),
                enoughPlayers ? AdminColors.Label : AdminColors.Warning, "dash-players") + 8;
            AddText(compo, inner, playersWidth, lineY, innerWidth - playersWidth,
                Lang.Get("canevents:admin-dash-running-count", s.RunningEvents, s.MaxConcurrentEvents),
                roomToRun ? AdminColors.Label : AdminColors.Warning, "dash-running");
            lineY += LineHeight + 6;

            var rowAnchor = inner.FlatCopy().WithFixedHeight(0);
            rowAnchor.fixedY = lineY;
            var buttons = new ButtonRow(compo, rowAnchor, innerWidth, 0, "dash-sched");

            string toggleCaption = Lang.Get(s.Enabled ? "canevents:admin-dash-turn-off" : "canevents:admin-dash-turn-on");
            bool enabled = s.Enabled;
            compo.AddButton(toggleCaption, new ActionConsumable(() =>
            {
                PanelActions.SetSchedule(Gui, !enabled);
                return true;
            }), buttons.Next(ButtonRow.ButtonWidth(toggleCaption)), EnumButtonStyle.Normal, "dash-toggle");

            string intervalLabel = Lang.Get("canevents:admin-dash-interval");
            double labelWidth = AdminFonts.Body.GetTextExtents(intervalLabel).Width + 6;
            var labelBounds = buttons.Next(labelWidth);
            labelBounds.fixedY += 6;
            compo.AddStaticText(intervalLabel, AdminFonts.Hint, labelBounds, "dash-interval-label");

            foreach (int m in SchedulerIntervals)
            {
                int minutes = m;
                string caption = Lang.Get("canevents:admin-dash-interval-min", minutes);
                string key = "dash-interval-" + minutes;
                compo.AddButton(caption, new ActionConsumable(() =>
                {
                    PanelActions.SetScheduleInterval(Gui, minutes);
                    return true;
                }), buttons.Next(ButtonRow.ButtonWidth(caption)), EnumButtonStyle.Normal, key);
                if (s.IntervalMinutes == minutes) compo.GetButton(key)?.SetActive(true);
            }
            lineY = buttons.Bounds.fixedY + ButtonRow.ButtonExtent;

            if (rotation != null)
            {
                lineY += RotationGap;
                double rotationHeight = ButtonRow.TextHeight(rotation, innerWidth);
                var b = inner.FlatCopy().WithFixedHeight(rotationHeight);
                b.fixedY = lineY;
                compo.AddStaticText(rotation, AdminFonts.Hint, b, "dash-rotation");
                lineY += rotationHeight;
            }

            if (signups.Count > 0) BuildSignupRows(ctx, inner, lineY + 8);

            return y + height + Card.Gap;
        }

        private void BuildSignupRows(PageBuildContext ctx, ElementBounds inner, double y)
        {
            var compo = ctx.Compo;
            AddText(compo, inner, 0, y, inner.fixedWidth, Lang.Get("canevents:admin-dash-signups-title"),
                AdminColors.Section, "dash-signups-title");
            y += LineHeight;

            string startCaption = Lang.Get("canevents:admin-dash-start-now");
            string cancelCaption = Lang.Get("canevents:admin-dash-cancel-signup");
            string cancelArmed = Lang.Get("canevents:admin-dash-cancel-signup-armed");
            double startWidth = ButtonRow.ButtonWidth(startCaption);
            double cancelWidth = ArmedTextButton.Width(cancelCaption, cancelArmed);
            double textWidth = Math.Max(120, inner.fixedWidth - startWidth - cancelWidth - 16);

            for (int i = 0; i < signups.Count; i++)
            {
                string code = signups[i].code;
                double rowY = y + i * SignupRowHeight;

                // TimeText to match the player HUD/chat format.
                var textBounds = inner.FlatCopy().WithFixedSize(textWidth, LineHeight);
                textBounds.fixedY = rowY + 5;
                AddLiveText(compo, textBounds, "dash-signup-" + code, AdminFonts.Body, () => SignupText(code));

                var startBounds = inner.FlatCopy().WithFixedSize(startWidth, ButtonRow.ButtonHeight);
                startBounds.fixedX += textWidth + 8;
                startBounds.fixedY = rowY;
                compo.AddButton(startCaption, new ActionConsumable(() =>
                {
                    PanelActions.ForceStart(Gui, code);
                    return true;
                }), startBounds, EnumButtonStyle.Normal, "dash-start-" + code);

                var cancelBounds = inner.FlatCopy().WithFixedSize(cancelWidth, ButtonRow.ButtonHeight);
                cancelBounds.fixedX += textWidth + 8 + startWidth + 8;
                cancelBounds.fixedY = rowY;
                ArmedTextButton.Add(ctx, cancelBounds, "dash-cancel-" + code, cancelCaption,
                    () => PanelActions.CancelSignup(Gui, code), cancelArmed);
            }
        }

        private string SignupText(string code)
        {
            foreach (var (c, left) in signups)
            {
                if (c != code) continue;
                return left < 0f
                    ? Lang.Get("canevents:admin-dash-signup-open-ended", code)
                    : Lang.Get("canevents:admin-dash-signup-left", code, TimeText.Short(left));
            }
            return code;
        }

        private void BuildRunningCard(PageBuildContext ctx, ElementBounds column, double y, double height)
        {
            var compo = ctx.Compo;
            ElementBounds inner = Card.Frame(compo, column, y, height, Lang.Get("canevents:admin-dash-running-title"));
            double innerWidth = inner.fixedWidth;
            double lineY = inner.fixedY;

            var running = Net.EventList.Value?.Events?.Where(e => e.EventId != 0).ToArray();
            if (running == null || running.Length == 0)
            {
                AddText(compo, inner, 0, lineY, innerWidth, Lang.Get("canevents:admin-dash-running-none"),
                    AdminColors.Muted, "dash-running-none");
                lineY += LineHeight;
            }
            else
            {
                int fit = Math.Max(1, (int)((inner.fixedHeight - ButtonRow.ButtonExtent - 6) / LineHeight));
                int shown = running.Length <= fit ? running.Length : fit - 1;
                for (int i = 0; i < shown; i++)
                {
                    var re = running[i];
                    string arena = string.IsNullOrEmpty(re.ArenaName) ? Lang.Get("canevents:admin-dash-no-arena") : re.ArenaName;
                    AddText(compo, inner, 0, lineY, innerWidth,
                        "• " + Lang.Get("canevents:admin-dash-running-line", re.GameModeCode, arena, re.CurrentPlayers),
                        AdminColors.Value, "dash-running-" + i);
                    lineY += LineHeight;
                }
                if (shown < running.Length)
                {
                    AddText(compo, inner, 0, lineY, innerWidth,
                        Lang.Get("canevents:admin-dash-running-more", running.Length - shown), AdminColors.Muted, "dash-running-more");
                    lineY += LineHeight;
                }
            }

            string caption = Lang.Get("canevents:admin-dash-stop-all");
            string armed = Lang.Get("canevents:admin-dash-stop-all-armed");
            var stopBounds = inner.FlatCopy().WithFixedSize(ArmedTextButton.Width(caption, armed), ButtonRow.ButtonHeight);
            stopBounds.fixedY = lineY + 6;
            ArmedTextButton.Add(ctx, stopBounds, "dash-stopall", caption, () => PanelActions.StopAll(Gui), armed);
        }

        private void BuildLogCard(PageBuildContext ctx, ElementBounds column, double y, double height)
        {
            var compo = ctx.Compo;
            ElementBounds inner = Card.Frame(compo, column, y, height, Lang.Get("canevents:admin-dash-log-title"));
            double innerWidth = inner.fixedWidth;

            var log = Gui.ActionLog;
            if (log.Count == 0)
            {
                string empty = Lang.Get("canevents:admin-dash-log-empty");
                double h = ButtonRow.TextHeight(empty, innerWidth);
                var b = inner.FlatCopy().WithFixedHeight(h);
                compo.AddStaticText(empty, AdminFonts.Hint, b, "dash-log-empty");
                return;
            }

            int fit = Math.Max(1, (int)(inner.fixedHeight / LineHeight));
            int limit = Math.Min(LogLinesShown, fit);
            double timeWidth = AdminFonts.Body.GetTextExtents("00:00:00").Width + 8;
            double lineY = inner.fixedY;
            int shown = 0;
            for (int i = log.Count - 1; i >= 0 && shown < limit; i--, shown++)
            {
                var (time, ok, text) = log[i];
                AddText(compo, inner, 0, lineY, timeWidth, time, AdminColors.Muted, "dash-log-time-" + shown);
                AddText(compo, inner, timeWidth, lineY, innerWidth - timeWidth, text ?? "",
                    ok ? AdminColors.Success : AdminColors.Danger, "dash-log-text-" + shown);
                lineY += LineHeight;
            }
        }

        private ServerSettingsMsg syncedSettings;

        private void BuildServerSettings(PageBuildContext ctx)
        {
            var compo = ctx.Compo;
            double top = ctx.Current.fixedY;
            double x0 = ctx.Main.fixedX;
            double width = ctx.Width;

            string hint = Lang.Get("canevents:admin-srv-hint");
            double hintHeight = ButtonRow.TextHeight(hint, width);
            compo.AddStaticText(hint, AdminFonts.Hint, ElementBounds.Fixed(x0, top, width, hintHeight), "dash-srv-hint");
            double y = top + hintHeight + 6;

            string reload = Lang.Get("canevents:admin-srv-reload");
            var reloadBounds = ElementBounds.Fixed(x0, y, ButtonRow.ButtonWidth(reload), ButtonRow.ButtonHeight);
            compo.AddButton(reload, new ActionConsumable(() =>
            {
                Net.SendRequestServerSettings();
                return true;
            }), reloadBounds, EnumButtonStyle.Normal, "dash-srv-reload");
            y += ButtonRow.ButtonExtent + 4;

            var msg = Net.ServerSettings.Value;
            if (msg?.Rows == null)
            {
                compo.AddStaticText(Lang.Get("canevents:admin-srv-loading"),
                    CairoFont.WhiteSmallText().WithColor(AdminColors.Muted),
                    ElementBounds.Fixed(x0, y, width, LineHeight), "dash-srv-loading");
                return;
            }

            var rows = ServerSettingRows(msg);
            SyncServerEditLine(msg, rows);

            var column = ctx.Column(0, Math.Min(ServerTableWidth, width));
            var inner = Card.Frame(compo, column, y, ctx.Bottom - y, Lang.Get("canevents:admin-srv-title"));
            SettingsTable.Add(ctx, inner, ServerTableKey, rows);
        }

        // Reload the edited row on each new packet so a rejected value doesn't stay in the field.
        private void SyncServerEditLine(ServerSettingsMsg msg, List<SettingRow> rows)
        {
            if (ReferenceEquals(syncedSettings, msg)) return;
            syncedSettings = msg;

            State.Drafts.Remove(ServerTableKey + "-value");
            if (!State.SettingsEditKey.TryGetValue(ServerTableKey, out string key)) return;
            var row = rows.FirstOrDefault(r => r.Id == key);
            if (row == null) State.SettingsEditKey.Remove(ServerTableKey);
            else State.SettingsEditValue[ServerTableKey] = row.CurrentValue();
        }

        // Setters send immediately and also update the local value so the redraw doesn't wait for the echo.
        private List<SettingRow> ServerSettingRows(ServerSettingsMsg msg)
        {
            var rows = new List<SettingRow>();
            string group = null;
            foreach (var r in msg.Rows)
            {
                if (r == null || string.IsNullOrEmpty(r.Key)) continue;
                if (r.GroupLangKey != group)
                {
                    group = r.GroupLangKey;
                    rows.Add(SettingRow.Group(Lang.Get(group ?? "")));
                }

                var src = r;
                string key = r.Key;
                Action<string> send = v =>
                {
                    src.Value = v;
                    Net.SendSetServerSetting(key, v);
                };

                string label = Lang.Get(r.LabelLangKey ?? key);
                if (r.PendingRestart) label += " " + Lang.Get("canevents:admin-srv-mark-pending");
                else if (r.RequiresRestart) label += " " + Lang.Get("canevents:admin-srv-mark-restart");

                string tip = Lang.Get(r.DescLangKey ?? "") + "\n"
                    + Lang.Get(r.PendingRestart ? "canevents:admin-srv-tip-pending"
                        : r.RequiresRestart ? "canevents:admin-srv-tip-restart"
                        : "canevents:admin-srv-tip-live");

                SettingRow row;
                switch ((ServerSettingKind)r.Kind)
                {
                    case ServerSettingKind.Flag:
                        row = SettingRow.Flag(key, label, () => src.Value == "true",
                            v => send(v ? "true" : "false"), tip);
                        break;
                    case ServerSettingKind.Int:
                        row = SettingRow.Int(key, label,
                            () => int.TryParse(src.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? i : 0,
                            v => send(v.ToString(CultureInfo.InvariantCulture)),
                            (int)r.Min, (int)r.Max, tip);
                        break;
                    case ServerSettingKind.Double:
                        row = SettingRow.Dbl(key, label,
                            () => double.TryParse(src.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : 0,
                            v => send(v.ToString("R", CultureInfo.InvariantCulture)),
                            r.Min, r.Max, tip);
                        break;
                    case ServerSettingKind.Choice:
                        var options = (r.Options ?? Array.Empty<string>())
                            .Select(o => new SettingOption(o, Lang.Get(("canevents:admin-srv-opt-" + key + "-" + o).ToLowerInvariant())))
                            .ToArray();
                        row = SettingRow.Choice(key, label, () => src.Value, v => send(v), options);
                        row.Tooltip = tip;
                        break;
                    default:
                        row = SettingRow.Text(key, label, () => src.Value, v => send((v ?? "").Trim()), tooltip: tip);
                        break;
                }
                rows.Add(row);
            }
            return rows;
        }

        private void AddRefreshButton(GuiComposer compo, ElementBounds column, double cardY)
        {
            string caption = Lang.Get("canevents:admin-dash-refresh");
            double width = CairoFont.SmallButtonText(EnumButtonStyle.Small).GetTextExtents(caption).Width + 20;
            var b = column.FlatCopy().WithFixedSize(width, 20);
            b.fixedX += column.fixedWidth - Card.Padding - width;
            b.fixedY = cardY + Card.Padding;
            compo.AddSmallButton(caption, new ActionConsumable(() =>
            {
                Net.RequestScheduleState();
                return true;
            }), b, EnumButtonStyle.Small, "dash-refresh");
        }

        // Single ellipsized line, full text on hover. Returns the drawn width.
        private static double AddText(GuiComposer compo, ElementBounds inner, double x, double y, double maxWidth,
                                      string text, double[] color, string key)
        {
            var font = CairoFont.WhiteSmallText().WithColor(color);
            string shown = Card.Fit(text, font, maxWidth);
            double width = Math.Min(maxWidth, font.GetTextExtents(shown ?? "").Width + 4);

            var b = inner.FlatCopy().WithFixedSize(Math.Max(1, width), LineHeight);
            b.fixedX += x;
            b.fixedY = y;
            compo.AddStaticText(shown ?? "", font, b, key);
            if (shown != text) Tooltip.Add(compo, text, b, key + "-tip");
            return width;
        }

        private static string FormatDuration(float seconds)
        {
            if (seconds < 0) return "-";
            int total = (int)seconds;
            return total >= 60
                ? Lang.Get("canevents:admin-dash-duration-min", total / 60, (total % 60).ToString("00"))
                : Lang.Get("canevents:admin-dash-duration-sec", total);
        }
    }
}
