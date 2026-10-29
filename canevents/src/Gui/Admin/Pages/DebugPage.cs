using System;
using canevents.src.Gui.Admin.Widgets;
using canevents.src.Network;
using canevents.src.Tournaments;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace canevents.src.Gui.Admin.Pages
{
    // Bot lobbies are excluded from Elo/standings by the result recorder; bot entrants pay no fee and get no prize.
    public sealed class DebugPage : CANGuiPage
    {
        private const double LineHeight = 18;
        private const double FieldHeight = 26;
        private const double LineGap = 6;
        private const int MaxLogLines = 8;

        private string eventCode = "";
        private int botCount = 3;

        // Index into BotModeKeys, also the BotMatchMode value.
        private int botMode;

        private static readonly string[] BotModeKeys = { "play", "watch", "unattended" };

        private long tournamentId;
        private int tournamentBots = 7;

        private static readonly string[] TourStateKeys =
            { "draft", "signup", "seeded", "running", "finished", "cancelled" };

        public override void Watch(PageWatchList watch)
        {
            watch.Add(Net.ScheduleState).Add(Net.AdminEventList).Add(Net.TournamentList);
        }

        protected override void BuildContent(PageBuildContext ctx)
        {
            double y = ctx.Current.fixedY + 4;

            var full = ctx.Column(0, ctx.Width);
            y = BuildState(ctx, full, y);

            double half = (ctx.Width - Card.ColumnGap) / 2;
            var left = ctx.Column(0, half);
            var right = ctx.Column(half + Card.ColumnGap, half);

            double leftEnd = BuildMatches(ctx, left, y);
            double rightEnd = BuildTournaments(ctx, right, y);
            y = Math.Max(leftEnd, rightEnd);

            BuildActionLog(ctx, full, y);
        }

        private double BuildState(PageBuildContext ctx, ElementBounds column, double y)
        {
            var compo = ctx.Compo;
            double innerWidth = column.fixedWidth - Card.Padding * 2;
            var s = Net.ScheduleState.Value;

            string hint = Lang.Get("canevents:admin-debug-state-hint");
            double hintHeight = ButtonRow.TextHeight(hint, innerWidth);

            string stopCaption = s == null ? "" : Lang.Get("canevents:admin-debug-stop-all");
            string stopArmed = s == null ? "" : Lang.Get("canevents:admin-debug-stop-all-armed", s.RunningEvents);
            string schedCaption = s == null ? "" : Lang.Get(s.Enabled ? "canevents:admin-debug-pause" : "canevents:admin-debug-resume");

            double content = hintHeight + LineGap
                + (s == null
                    ? LineHeight * 2
                    : LineHeight + LineGap + ButtonRow.HeightFor(innerWidth, 0, schedCaption,
                        ArmedTextButton.WiderCaption(stopCaption, stopArmed)));
            double height = Card.HeightAround(content);

            ElementBounds inner = Card.Frame(compo, column, y, height, Lang.Get("canevents:admin-debug-state-title"));
            Card.AddRefresh(ctx, column, y, "dbg-state-refresh", () => Net.RequestScheduleState());

            compo.AddStaticText(hint, AdminFonts.Hint, inner.FlatCopy().WithFixedHeight(hintHeight), "dbg-state-hint");
            double rowY = inner.fixedY + hintHeight + LineGap;

            if (s == null)
            {
                Empty(ctx, inner, rowY, "dbg-state-empty",
                    Lang.Get("canevents:admin-debug-state-empty"), Lang.Get("canevents:admin-debug-press-refresh"));
                return y + height + Card.Gap;
            }

            double x = inner.fixedX;
            x = Inline(ctx, x, rowY, "dbg-running-label", Lang.Get("canevents:admin-debug-running"), AdminColors.Label);
            x = Inline(ctx, x, rowY, "dbg-running-value", s.RunningEvents + " / " + s.MaxConcurrentEvents,
                s.RunningEvents > 0 ? AdminColors.Success : AdminColors.Muted);
            x = Inline(ctx, x + 10, rowY, "dbg-sched-label", "|   " + Lang.Get("canevents:admin-debug-scheduler"), AdminColors.Muted);
            Inline(ctx, x, rowY, "dbg-sched-value",
                s.Enabled ? Lang.Get("canevents:admin-debug-scheduler-on", s.IntervalMinutes) : Lang.Get("canevents:admin-debug-scheduler-off"),
                s.Enabled ? AdminColors.Success : AdminColors.Muted);

            var rowAnchor = inner.FlatCopy().WithFixedHeight(0);
            rowAnchor.fixedY = rowY + LineHeight + LineGap;
            var buttons = new ButtonRow(compo, rowAnchor, innerWidth, 0, "dbg-state-btn");

            bool enabled = s.Enabled;
            Button(ctx, buttons, "dbg-sched-toggle", schedCaption, () => PanelActions.SetSchedule(Gui, !enabled),
                Lang.Get("canevents:admin-debug-pause-tip"));

            // Armed (two-click), disabled when nothing is running.
            const string stopKey = "dbg-stop-all";
            var stopBounds = buttons.Next(ArmedTextButton.Width(stopCaption, stopArmed));
            ArmedTextButton.Add(ctx, stopBounds, stopKey, stopCaption, () => PanelActions.StopAll(Gui),
                stopArmed, Lang.Get("canevents:admin-debug-stop-all-tip"));
            if (s.RunningEvents == 0) compo.GetButton(stopKey).Enabled = false;

            return y + height + Card.Gap;
        }

        private double BuildMatches(PageBuildContext ctx, ElementBounds column, double y)
        {
            var compo = ctx.Compo;
            double innerWidth = column.fixedWidth - Card.Padding * 2;
            var events = Net.AdminEventList.Value?.Events;
            string title = Lang.Get("canevents:admin-debug-matches-title");

            if (events == null || events.Length == 0)
            {
                double h = Card.HeightAround(LineHeight * 2);
                ElementBounds emptyInner = Card.Frame(compo, column, y, h, title);
                Card.AddRefresh(ctx, column, y, "dbg-ev-refresh", () => Net.RequestAdminEventList());
                Empty(ctx, emptyInner, emptyInner.fixedY, "dbg-ev-empty",
                    Lang.Get("canevents:admin-debug-events-empty"), Lang.Get("canevents:admin-debug-press-refresh"));
                return y + h + Card.Gap;
            }

            var codes = new string[events.Length];
            for (int i = 0; i < events.Length; i++) codes[i] = events[i].Code ?? "";
            // Fall back to what the dropdown shows.
            int index = Array.IndexOf(codes, eventCode);
            if (index < 0) { index = 0; eventCode = codes[0]; }

            string[] modeLabels = new string[BotModeKeys.Length];
            string[] modeValues = new string[BotModeKeys.Length];
            for (int i = 0; i < BotModeKeys.Length; i++)
            {
                modeLabels[i] = Lang.Get("canevents:admin-debug-mode-" + BotModeKeys[i]);
                modeValues[i] = i.ToString();
            }

            // Two lines reserved, summary is rewritten in place while typing.
            double summaryHeight = Math.Max(ButtonRow.TextHeight(Summary(), innerWidth), LineHeight * 2);

            string startCaption = Lang.Get("canevents:admin-debug-start-" + BotModeKeys[botMode]);
            string forceCaption = Lang.Get("canevents:admin-debug-force-start");
            double buttonsHeight = ButtonRow.HeightFor(innerWidth, 0, startCaption, forceCaption);

            // Warning line always reserved.
            double content = FieldHeight + LineGap + FieldHeight + LineGap + summaryHeight + LineGap + buttonsHeight
                             + LineHeight;
            double height = Card.HeightAround(content);

            ElementBounds inner = Card.Frame(compo, column, y, height, title);
            Card.AddRefresh(ctx, column, y, "dbg-ev-refresh", () => Net.RequestAdminEventList());

            double rowY = inner.fixedY;

            double labelWidth = 90;
            Label(ctx, inner, rowY, labelWidth, "dbg-ev-label", Lang.Get("canevents:admin-debug-event"));
            var evBounds = inner.FlatCopy().WithFixedSize(Math.Min(240, innerWidth - labelWidth), FieldHeight);
            evBounds.fixedX += labelWidth;
            evBounds.fixedY = rowY;
            compo.AddDropDown(codes, codes, index, (code, selected) =>
            {
                eventCode = code;
                Rebuild();
            }, evBounds, "dbg-ev-pick");

            rowY += FieldHeight + LineGap;
            Label(ctx, inner, rowY, labelWidth, "dbg-bots-label", Lang.Get("canevents:admin-debug-bots"));
            var botsBounds = inner.FlatCopy().WithFixedSize(60, FieldHeight);
            botsBounds.fixedX += labelWidth;
            botsBounds.fixedY = rowY;
            // No rebuild here: it would fire between mouse-down and mouse-up of the unfocusing click and eat it.
            NumberField.AddInt(ctx, botsBounds, "dbg-bots", () => botCount, v =>
            {
                botCount = v;
                RefreshMatchLine(compo);
            }, 1, 32,
                Lang.Get(botMode == 0 ? "canevents:admin-debug-bots-tip-play" : "canevents:admin-debug-bots-tip-solo")
                + "\n\n" + Lang.Get("canevents:admin-debug-bots-tip"));

            double partX = labelWidth + 60 + 12;
            string partCaption = Lang.Get("canevents:admin-debug-part");
            double partLabelWidth = AdminFonts.Body.GetTextExtents(partCaption).Width + 8;
            var partLabel = inner.FlatCopy().WithFixedSize(partLabelWidth, 20);
            partLabel.fixedX += partX;
            partLabel.fixedY = rowY + 4;
            compo.AddStaticText(partCaption, AdminFonts.Body, partLabel, "dbg-part-label");

            var modeBounds = inner.FlatCopy().WithFixedSize(Math.Max(100, Math.Min(170, innerWidth - partX - partLabelWidth)), FieldHeight);
            modeBounds.fixedX += partX + partLabelWidth;
            modeBounds.fixedY = rowY;
            compo.AddDropDown(modeValues, modeLabels, botMode, (value, selected) =>
            {
                if (int.TryParse(value, out int m) && m >= 0 && m < BotModeKeys.Length) botMode = m;
                Rebuild();
            }, modeBounds, "dbg-mode-pick");
            Tooltip.Add(compo, Lang.Get("canevents:admin-debug-part-tip"), partLabel, "tip-dbg-part");

            rowY += FieldHeight + LineGap;
            var summaryBounds = inner.FlatCopy().WithFixedHeight(summaryHeight);
            summaryBounds.fixedY = rowY;
            compo.AddDynamicText("", AdminFonts.Hint, summaryBounds, "dbg-summary");

            rowY += summaryHeight + LineGap;
            var rowAnchor = inner.FlatCopy().WithFixedHeight(0);
            rowAnchor.fixedY = rowY;
            var buttons = new ButtonRow(compo, rowAnchor, innerWidth, 0, "dbg-ev-btn");

            // Read at click time, the count may have changed since this build.
            Button(ctx, buttons, BotMatchKey, startCaption, () =>
            {
                string code = eventCode;
                int count = botCount;
                int mode = botMode;
                if (TooFewBots()) return;
                Act(Lang.Get("canevents:admin-debug-log-bot-match", code, count, modeLabels[mode].ToLowerInvariant()),
                    () => Net.SendAdminAction(AdminAction.BotMatch, code, count, (BotMatchMode)mode));
            }, null);

            Button(ctx, buttons, "dbg-force-start", forceCaption, () => PanelActions.ForceStart(Gui, eventCode),
                Lang.Get("canevents:admin-debug-force-start-tip"));

            var warn = inner.FlatCopy().WithFixedHeight(LineHeight);
            warn.fixedY = rowY + buttonsHeight;
            compo.AddDynamicText("", CairoFont.WhiteSmallText().WithColor(AdminColors.Warning), warn, "dbg-too-few");

            ctx.AfterCompose(() => RefreshMatchLine(compo));
            return y + height + Card.Gap;
        }

        private const string BotMatchKey = "dbg-bot-match";

        // Without the admin playing, one bot ends the match instantly.
        private bool TooFewBots() => botMode != 0 && botCount < 2;

        private string Summary()
        {
            if (botMode == 0) return Lang.Get("canevents:admin-debug-summary-play", botCount, eventCode);
            return Lang.Get(botMode == 1 ? "canevents:admin-debug-summary-watch" : "canevents:admin-debug-summary-unattended",
                botCount, eventCode);
        }

        private void RefreshMatchLine(GuiComposer compo)
        {
            bool tooFew = TooFewBots();
            compo.GetDynamicText("dbg-summary")?.SetNewText(Summary());
            compo.GetDynamicText("dbg-too-few")?.SetNewText(tooFew ? Lang.Get("canevents:admin-debug-too-few-bots") : "");
            var start = compo.GetButton(BotMatchKey);
            if (start != null) start.Enabled = !tooFew;
        }

        private double BuildTournaments(PageBuildContext ctx, ElementBounds column, double y)
        {
            var compo = ctx.Compo;
            double innerWidth = column.fixedWidth - Card.Padding * 2;
            var list = Net.TournamentList.Value;
            string title = Lang.Get("canevents:admin-debug-tour-title");

            Action refresh = () =>
            {
                Net.RequestTournamentList();
                if (tournamentId > 0) Net.RequestTournamentBracket(tournamentId);
            };

            if (list == null || list.Count == 0)
            {
                double h = Card.HeightAround(LineHeight * 2);
                ElementBounds emptyInner = Card.Frame(compo, column, y, h, title);
                Card.AddRefresh(ctx, column, y, "dbg-tour-refresh", refresh);
                Empty(ctx, emptyInner, emptyInner.fixedY, "dbg-tour-empty",
                    Lang.Get("canevents:admin-debug-tour-empty"), Lang.Get("canevents:admin-debug-tour-empty-hint"));
                return y + h + Card.Gap;
            }

            var labels = new string[list.Count];
            var values = new string[list.Count];
            int index = -1;
            for (int i = 0; i < list.Count; i++)
            {
                labels[i] = "#" + list.Ids[i] + " " + list.NameAt(i) + " [" + StateLabel(list.StateAt(i)) + "]";
                values[i] = list.Ids[i].ToString();
                if (list.Ids[i] == tournamentId) index = i;
            }
            if (index < 0) { index = 0; tournamentId = list.Ids[0]; }

            var state = (TournamentState)list.StateAt(index);
            bool canAddBots = state == TournamentState.Draft || state == TournamentState.Signup
                           || state == TournamentState.Seeded;
            bool canCall = state == TournamentState.Running;

            string fillCaption = Lang.Get("canevents:admin-debug-fill-bots");
            string callCaption = Lang.Get("canevents:admin-debug-call-next");
            double buttonsHeight = ButtonRow.HeightFor(innerWidth, 0, fillCaption, callCaption);
            int hintLines = (canAddBots ? 0 : 1) + (canCall ? 0 : 1);

            double content = FieldHeight + LineGap + FieldHeight + LineGap + buttonsHeight + hintLines * LineHeight;
            double height = Card.HeightAround(content);

            ElementBounds inner = Card.Frame(compo, column, y, height, title);
            Card.AddRefresh(ctx, column, y, "dbg-tour-refresh", refresh);

            double rowY = inner.fixedY;
            double labelWidth = 110;

            Label(ctx, inner, rowY, labelWidth, "dbg-tour-label", Lang.Get("canevents:admin-debug-tournament"));
            var pick = inner.FlatCopy().WithFixedSize(innerWidth - labelWidth, FieldHeight);
            pick.fixedX += labelWidth;
            pick.fixedY = rowY;
            compo.AddDropDown(values, labels, index, (value, selected) =>
            {
                if (long.TryParse(value, out long id)) tournamentId = id;
                Rebuild();
            }, pick, "dbg-tour-pick");

            rowY += FieldHeight + LineGap;
            Label(ctx, inner, rowY, labelWidth, "dbg-tbots-label", Lang.Get("canevents:admin-debug-tour-bots"));
            var botsBounds = inner.FlatCopy().WithFixedSize(60, FieldHeight);
            botsBounds.fixedX += labelWidth;
            botsBounds.fixedY = rowY;
            NumberField.AddInt(ctx, botsBounds, "dbg-tour-bots", () => tournamentBots, v => tournamentBots = v, 1, 64,
                Lang.Get("canevents:admin-debug-tour-bots-tip"));

            rowY += FieldHeight + LineGap;
            var rowAnchor = inner.FlatCopy().WithFixedHeight(0);
            rowAnchor.fixedY = rowY;
            var buttons = new ButtonRow(compo, rowAnchor, innerWidth, 0, "dbg-tour-btn");

            long id0 = tournamentId;
            Button(ctx, buttons, "dbg-fill-bots", fillCaption, () =>
            {
                int bots = tournamentBots;
                Act(Lang.Get("canevents:admin-debug-log-fill-bots", id0, bots),
                    () => SendTourAction("bots", id0, bots));
            }, null, enabled: canAddBots);

            Button(ctx, buttons, "dbg-call-next", callCaption, () =>
            {
                Act(Lang.Get("canevents:admin-debug-log-force-call", id0),
                    () => SendTourAction("forcecall", id0));
            }, Lang.Get("canevents:admin-debug-call-next-tip"), enabled: canCall);

            double hintY = rowY + buttonsHeight;
            if (!canAddBots)
            {
                Hint(ctx, inner, hintY, "dbg-tour-hint-bots", Lang.Get("canevents:admin-debug-bots-before-start"));
                hintY += LineHeight;
            }
            if (!canCall)
            {
                Hint(ctx, inner, hintY, "dbg-tour-hint-call", Lang.Get("canevents:admin-debug-call-while-running"));
            }

            return y + height + Card.Gap;
        }

        // For "bots" the wire reuses matchId as the bot count.
        private void SendTourAction(string wireName, long id, int matchId = 0)
        {
            Net.SendTournamentAdminAction(new TournamentAdminActionMsg
            {
                Action = wireName,
                TournamentId = id,
                MatchId = matchId
            });
            Net.RequestTournamentList();
        }

        private static string StateLabel(int state)
            => state >= 0 && state < TourStateKeys.Length
                ? Lang.Get("canevents:admin-debug-tour-state-" + TourStateKeys[state])
                : "?";

        private void BuildActionLog(PageBuildContext ctx, ElementBounds column, double y)
        {
            var compo = ctx.Compo;
            var log = Gui.ActionLog;

            double room = ctx.Bottom - y - Card.Padding * 2 - Card.HeaderHeight;
            int fit = (int)Math.Floor(room / Card.LineHeight);
            if (fit < 1) return;

            int lines = Math.Min(MaxLogLines, Math.Min(fit, Math.Max(1, log.Count)));
            ElementBounds inner = Card.Frame(compo, column, y, Card.HeightAround(lines * Card.LineHeight),
                Lang.Get("canevents:admin-debug-log-title"));

            if (log.Count == 0)
            {
                compo.AddStaticText(Lang.Get("canevents:admin-debug-log-empty"), AdminFonts.Hint,
                    inner.FlatCopy().WithFixedHeight(Card.LineHeight), "dbg-log-empty");
                return;
            }

            const double timeWidth = 70;
            for (int n = 0, i = log.Count - 1; n < lines && i >= 0; n++, i--)
            {
                var (time, ok, text) = log[i];
                double rowY = inner.fixedY + n * Card.LineHeight;

                var timeBounds = inner.FlatCopy().WithFixedSize(timeWidth, Card.LineHeight);
                timeBounds.fixedY = rowY;
                compo.AddStaticText(time, AdminFonts.Hint, timeBounds, "dbg-log-time-" + n);

                var font = CairoFont.WhiteSmallText().WithColor(ok ? AdminColors.Success : AdminColors.Danger);
                double textWidth = inner.fixedWidth - timeWidth;
                var textBounds = inner.FlatCopy().WithFixedSize(textWidth, Card.LineHeight);
                textBounds.fixedX += timeWidth;
                textBounds.fixedY = rowY;
                Card.AddFitLine(compo, text, font, textBounds, "dbg-log-text-" + n, "dbg-log-tip-" + n);
            }
        }

        private static void Button(PageBuildContext ctx, ButtonRow row, string key, string caption, Action onClick,
                                   string tooltip, bool enabled = true)
        {
            var bounds = row.Next(ButtonRow.ButtonWidth(caption));
            var gui = ctx.Gui;
            ctx.Compo.AddButton(caption, new ActionConsumable(() =>
            {
                onClick();
                // Refresh the log card.
                gui.RequestRebuild();
                return true;
            }), bounds, EnumButtonStyle.Normal, key);
            if (!enabled) ctx.Compo.GetButton(key).Enabled = false;
            if (tooltip != null) Tooltip.Add(ctx.Compo, tooltip, bounds, "tip-" + key);
        }

        // Returns x for the next piece.
        private static double Inline(PageBuildContext ctx, double x, double y, string key, string text, double[] color)
        {
            var font = CairoFont.WhiteSmallText().WithColor(color);
            double w = font.GetTextExtents(text).Width + 6;
            ctx.Compo.AddStaticText(text, font, ElementBounds.Fixed(x, y, w, LineHeight), key);
            return x + w;
        }

        private static void Label(PageBuildContext ctx, ElementBounds inner, double y, double width, string key, string text)
        {
            var b = inner.FlatCopy().WithFixedSize(width, 20);
            b.fixedY = y + 4;
            ctx.Compo.AddStaticText(Card.Fit(text, AdminFonts.Body, width - 4), AdminFonts.Body, b, key);
        }

        private static void Hint(PageBuildContext ctx, ElementBounds inner, double y, string key, string text)
        {
            var b = inner.FlatCopy().WithFixedHeight(LineHeight);
            b.fixedY = y;
            ctx.Compo.AddStaticText(text, AdminFonts.Hint, b, key);
        }

        private static void Empty(PageBuildContext ctx, ElementBounds inner, double y, string key, string text, string advice)
        {
            var b = inner.FlatCopy().WithFixedHeight(LineHeight);
            b.fixedY = y;
            ctx.Compo.AddStaticText(text, CairoFont.WhiteSmallText().WithColor(AdminColors.Label), b, key);
            Hint(ctx, inner, y + LineHeight, key + "-hint", advice);
        }
    }
}
