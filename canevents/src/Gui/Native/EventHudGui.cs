using System;
using System.Linq;
using System.Text;
using canevents.src.EventManagers;
using canevents.src.Gui;
using canevents.src.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace canevents.src.Gui.Native
{
    // One composer per panel so each can be dragged. Recompose on shape change only; numbers are dynamic text.
    public class EventHudGui : HudElement
    {
        private readonly ClientNetworkHandler networkHandler;
        private readonly HudLayout layout;

        // Ticked locally between packets.
        private float timeRemaining, maxDuration, countdown;
        private long lastEventId;

        // In-match hold (pre-fight cage, HG pedestal freeze); start cry shows on release for CryShowSeconds.
        private float holdRemaining, cryRemaining;
        private bool HoldActive => holdRemaining > 0f;
        private EventStateUpdateMsg lastStateSeen;
        private const float CryShowSeconds = 1.5f;

        private void EndHold()
        {
            holdRemaining = 0f;
            cryRemaining = CryShowSeconds;
            // The fight starts now; a sound so nobody misses it while looking elsewhere.
            capi.Gui.PlaySound("effect/receptionbell");
        }

        // Shape of the current composition.
        private int composedScoreRows = -1;
        private int composedFeedRows = -1;
        private bool composedZone, composedTimer, composedCountdown, composedBoss;
        // Last percent drawn on the boss bar; same once-per-change rule as barSecond.
        private int bossPercent = -1;
        private int composedFrameW, composedFrameH;

        // Colours are baked into the composed font/statbar, so a colour change is a shape change.
        private bool composedUrgent;
        private bool composedOutside;   // outside the storm ring
        private int composedAliveHash = -1;

        // -1 = not set since the last compose. See UpdateTexts.
        private int barSecond = -1;

        // Fraction of the match left when the timer turns red.
        private const float UrgentFraction = 0.15f;

        private const int MaxFeedRows = 5;

        private readonly Action onStateUpdated;

        public EventHudGui(ICoreClientAPI capi, ClientNetworkHandler networkHandler, HudLayout layout) : base(capi)
        {
            this.networkHandler = networkHandler;
            this.layout = layout;

            // A closed dialog gets no render calls, so open from the state packet.
            onStateUpdated = () =>
            {
                if (networkHandler.CurrentEventState == null)
                {
                    if (IsOpened()) TryClose();
                    return;
                }
                if (!IsOpened()) ClientTrace.Step("EventHudGui open", () => { Compose(); TryOpen(); });
            };
            networkHandler.OnStateUpdated += onStateUpdated;
        }

        public override void Dispose()
        {
            base.Dispose();
            networkHandler.OnStateUpdated -= onStateUpdated;
        }

        public override bool Focusable => false;
        public override bool ShouldReceiveKeyboardEvents() => false;

        public void OnLayoutChanged() => Compose();

        public override void OnRenderGUI(float deltaTime)
        {
            var state = networkHandler.CurrentEventState;

            if (state == null)
            {
                if (IsOpened()) TryClose();
                lastEventId = 0;
                return;
            }

            if (state.EventId != lastEventId)
            {
                timeRemaining = state.TimeRemainingSeconds;
                maxDuration = state.MaxDurationSeconds;
                countdown = state.CountdownSeconds;
                lastEventId = state.EventId;
            }

            // A packet without a hold while holding means release.
            if (!ReferenceEquals(state, lastStateSeen))
            {
                // Every fresh packet resyncs the clock: round modes restart it each round.
                if (lastStateSeen != null && state.EventState == EventState.Running)
                {
                    timeRemaining = state.TimeRemainingSeconds;
                    maxDuration = state.MaxDurationSeconds;
                }
                lastStateSeen = state;
                if (state.EventState == EventState.Running && state.HoldSeconds > 0f)
                    holdRemaining = state.HoldSeconds;
                else if (HoldActive)
                    EndHold();
            }

            if (state.EventState == EventState.LocationFound)
            {
                countdown = Math.Max(0f, countdown - deltaTime);
            }
            else if (state.EventState == EventState.Running)
            {
                timeRemaining = Math.Max(0f, timeRemaining - deltaTime);
                if (HoldActive)
                {
                    holdRemaining -= deltaTime;
                    // Predicted release; the server's release packet arrives later.
                    if (holdRemaining <= 0f) EndHold();
                }
                else if (cryRemaining > 0f)
                {
                    cryRemaining -= deltaTime;
                }
            }

            if (ShapeChanged()) Compose();
            UpdateTexts();

            base.OnRenderGUI(deltaTime);
        }

        private bool ShapeChanged()
        {
            var state = networkHandler.CurrentEventState;
            bool wantCountdown = WantCountdown(state);
            bool wantTimer = state?.EventState != EventState.LocationFound;
            bool wantZone = networkHandler.HgZone?.Active == true;
            int scoreRows = networkHandler.CurrentScoreboard?.Entries?.Length ?? 0;
            int feedRows = Math.Min(MaxFeedRows, networkHandler.KillFeed?.Count ?? 0);

            return wantCountdown != composedCountdown
                || wantTimer != composedTimer
                || wantZone != composedZone
                || WantBoss() != composedBoss
                || scoreRows != composedScoreRows
                || feedRows != composedFeedRows
                || IsUrgent() != composedUrgent
                || IsOutside() != composedOutside
                || AliveHash() != composedAliveHash
                || capi.Render.FrameWidth != composedFrameW
                || capi.Render.FrameHeight != composedFrameH;
        }

        private void Compose()
        {
            var state = networkHandler.CurrentEventState;
            composedCountdown = WantCountdown(state);
            composedTimer = state?.EventState != EventState.LocationFound;
            composedZone = networkHandler.HgZone?.Active == true;
            composedBoss = WantBoss();
            bossPercent = -1;
            composedScoreRows = networkHandler.CurrentScoreboard?.Entries?.Length ?? 0;
            composedFeedRows = Math.Min(MaxFeedRows, networkHandler.KillFeed?.Count ?? 0);
            composedUrgent = IsUrgent();
            composedOutside = IsOutside();
            composedAliveHash = AliveHash();
            composedFrameW = capi.Render.FrameWidth;
            composedFrameH = capi.Render.FrameHeight;

            // Rebuilt bar is empty; force the once-a-second update next frame.
            barSecond = -1;

            Composers.ClearComposers();

            if (composedCountdown) ComposeCountdown();
            if (composedTimer) ComposeTimer();
            if (composedZone) ComposeZone();
            if (composedBoss) ComposeBoss();
            if (composedScoreRows > 0) ComposeScoreboard();
            if (composedFeedRows > 0) ComposeKillFeed();
        }

        // Stored positions are normalised; GUI coords are unscaled, hence the divide by GUIScale.
        private ElementBounds PanelBounds(HudLayout.PanelPos pos, double width, double height,
                                          double defaultX, double defaultY)
        {
            double screenW = capi.Render.FrameWidth / RuntimeEnv.GUIScale;
            double screenH = capi.Render.FrameHeight / RuntimeEnv.GUIScale;

            double x = pos != null && pos.IsSet ? pos.X * screenW : defaultX;
            double y = pos != null && pos.IsSet ? pos.Y * screenH : defaultY;

            x = GameMath.Clamp(x, 0, Math.Max(0, screenW - width));
            y = GameMath.Clamp(y, 0, Math.Max(0, screenH - height));

            return ElementBounds.Fixed(x, y, width, height);
        }

        private void ComposeTimer()
        {
            const double w = 300, h = 54;
            double screenW = capi.Render.FrameWidth / RuntimeEnv.GUIScale;
            var bounds = PanelBounds(layout.Timer, w, h, screenW / 2 - w / 2, 10);
            var bg = ElementBounds.Fixed(0, 0, w, h);

            var titleFont = CairoFont.WhiteSmallText().WithColor(Palette.Header);
            var timeFont = CairoFont.WhiteSmallishText()
                .WithColor(composedUrgent ? Palette.Stop : Palette.Title)
                .WithOrientation(EnumTextOrientation.Center);
            // Team score (rounds won, team kills) on the right of the title line.
            var teamsFont = CairoFont.WhiteSmallText().WithColor(Palette.Title).WithOrientation(EnumTextOrientation.Right);

            Composers["timer"] = capi.Gui
                .CreateCompo("canevents-hud-timer", bounds)
                .AddGameOverlay(bg, Palette.HudBg)
                .BeginChildElements(bg)
                    .AddDynamicText("", titleFont, ElementBounds.Fixed(10, 4, w - 20, 16), "title")
                    .AddDynamicText("", teamsFont, ElementBounds.Fixed(10, 4, w - 20, 16), "teams")
                    .AddDynamicText("", timeFont, ElementBounds.Fixed(10, 21, w - 20, 20), "time")
                    .AddStatbar(ElementBounds.Fixed(10, 43, w - 20, 7),
                        composedUrgent ? Palette.Stop : Palette.Go, "bar")
                .EndChildElements()
                .Compose();
        }

        private void ComposeZone()
        {
            const double w = 240, h = 48;
            double screenW = capi.Render.FrameWidth / RuntimeEnv.GUIScale;
            var bounds = PanelBounds(layout.Zone, w, h, screenW / 2 - w / 2, 78);
            var bg = ElementBounds.Fixed(0, 0, w, h);

            var distFont = CairoFont.WhiteSmallishText()
                .WithColor(composedOutside ? Palette.Stop : Palette.Info)
                .WithOrientation(EnumTextOrientation.Center);
            var nextFont = CairoFont.WhiteSmallText()
                .WithColor(Palette.Header)
                .WithOrientation(EnumTextOrientation.Center);

            Composers["zone"] = capi.Gui
                .CreateCompo("canevents-hud-zone", bounds)
                .AddGameOverlay(bg, Palette.HudBg)
                .BeginChildElements(bg)
                    .AddDynamicText("", distFont, ElementBounds.Fixed(8, 5, w - 16, 20), "dist")
                    .AddDynamicText("", nextFont, ElementBounds.Fixed(8, 26, w - 16, 18), "next")
                .EndChildElements()
                .Compose();
        }

        private bool WantBoss() => !string.IsNullOrEmpty(networkHandler.CurrentScoreboard?.BossName);

        // Under the timer, where the storm panel would sit; the two never share a mode.
        private void ComposeBoss()
        {
            const double w = 360, h = 44;
            double screenW = capi.Render.FrameWidth / RuntimeEnv.GUIScale;
            var bounds = PanelBounds(null, w, h, screenW / 2 - w / 2, 70);
            var bg = ElementBounds.Fixed(0, 0, w, h);

            var nameFont = CairoFont.WhiteSmallText().WithColor(Palette.Title).WithOrientation(EnumTextOrientation.Center);

            Composers["boss"] = capi.Gui
                .CreateCompo("canevents-hud-boss", bounds)
                .AddGameOverlay(bg, Palette.HudBg)
                .BeginChildElements(bg)
                    .AddDynamicText("", nameFont, ElementBounds.Fixed(10, 4, w - 20, 18), "name")
                    .AddStatbar(ElementBounds.Fixed(10, 26, w - 20, 10), Palette.Stop, "hp")
                .EndChildElements()
                .Compose();
        }

        private void UpdateBossTexts()
        {
            var scoreboard = networkHandler.CurrentScoreboard;
            if (scoreboard == null) return;
            var compo = Composers["boss"];
            int percent = (int)Math.Ceiling(GameMath.Clamp(scoreboard.BossHealth, 0f, 1f) * 100);
            compo.GetDynamicText("name").SetNewText(scoreboard.BossName + "  " + percent + "%");

            var bar = compo.GetStatbar("hp");
            if (bar == null || percent == bossPercent) return;
            bossPercent = percent;
            bar.SetLineInterval(10);
            bar.SetValues(percent, 0, 100);
        }

        private void ComposeScoreboard()
        {
            const double w = 240;
            double h = 14 + composedScoreRows * 20;
            double screenW = capi.Render.FrameWidth / RuntimeEnv.GUIScale;
            var bounds = PanelBounds(layout.Scoreboard, w, h, screenW - w - 10, 10);
            var bg = ElementBounds.Fixed(0, 0, w, h);

            var composer = capi.Gui
                .CreateCompo("canevents-hud-score", bounds)
                .AddGameOverlay(bg, Palette.HudBg)
                .BeginChildElements(bg);

            var entries = networkHandler.CurrentScoreboard?.Entries;
            for (int i = 0; i < composedScoreRows; i++)
            {
                var entry = entries != null && i < entries.Length ? entries[i] : null;
                var font = CairoFont.WhiteSmallText().WithColor(
                    entry == null || !entry.Alive ? Palette.Idle : TeamColor(entry.TeamId));

                composer
                    .AddDynamicText("", font, ElementBounds.Fixed(10, 6 + i * 20, w - 52, 18), "row" + i)
                    .AddDynamicText("", font.Clone().WithOrientation(EnumTextOrientation.Right),
                        ElementBounds.Fixed(w - 42, 6 + i * 20, 32, 18), "kills" + i);
            }

            Composers["score"] = composer.EndChildElements().Compose();
        }

        private void ComposeKillFeed()
        {
            const double w = 360;
            double h = 12 + composedFeedRows * 20;
            double screenH = capi.Render.FrameHeight / RuntimeEnv.GUIScale;
            var bounds = PanelBounds(layout.KillFeed, w, h, 10, screenH - h - 60);
            var bg = ElementBounds.Fixed(0, 0, w, h);

            var composer = capi.Gui
                .CreateCompo("canevents-hud-feed", bounds)
                .AddGameOverlay(bg, Palette.HudBg)
                .BeginChildElements(bg);

            for (int i = 0; i < composedFeedRows; i++)
            {
                double age = composedFeedRows <= 1 ? 1 : (double)i / (composedFeedRows - 1);
                var font = CairoFont.WhiteSmallText().WithColor(new double[]
                {
                    Palette.Header[0], Palette.Header[1], Palette.Header[2], 0.45 + 0.55 * age
                });
                composer.AddDynamicText("", font, ElementBounds.Fixed(10, 5 + i * 20, w - 20, 18), "feed" + i);
            }

            Composers["feed"] = composer.EndChildElements().Compose();
        }

        // Before the teleport, and during a hold or its release cry.
        private bool WantCountdown(EventStateUpdateMsg state)
            => state?.EventState == EventState.LocationFound
               || (state?.EventState == EventState.Running && (HoldActive || cryRemaining > 0f));

        private void ComposeCountdown()
        {
            double screenW = capi.Render.FrameWidth / RuntimeEnv.GUIScale;
            double screenH = capi.Render.FrameHeight / RuntimeEnv.GUIScale;
            var bounds = ElementBounds.Fixed(screenW / 2 - 250, screenH / 3 - 60, 500, 165);

            var font = CairoFont.WhiteSmallText()
                .WithFontSize(90)
                .WithWeight(Cairo.FontWeight.Bold)
                .WithColor(Palette.Title)
                .WithStroke(new double[] { 0, 0, 0, 1 }, 3);
            var subFont = CairoFont.WhiteSmallText()
                .WithFontSize(26)
                .WithWeight(Cairo.FontWeight.Bold)
                .WithStroke(new double[] { 0, 0, 0, 1 }, 2);

            Composers["countdown"] = capi.Gui
                .CreateCompo("canevents-hud-countdown", bounds)
                .AddDynamicText("", font, EnumTextOrientation.Center, ElementBounds.Fixed(0, 0, 500, 120), "text")
                .AddDynamicText("", subFont, EnumTextOrientation.Center, ElementBounds.Fixed(0, 122, 500, 40), "sub")
                .Compose();
        }

        private bool IsUrgent()
        {
            if (maxDuration <= 0f) return false;
            return timeRemaining / maxDuration <= UrgentFraction;
        }

        private bool IsOutside()
        {
            var zone = networkHandler.HgZone;
            var entity = capi.World?.Player?.Entity;
            if (zone == null || !zone.Active || entity == null) return false;

            float since = (capi.ElapsedMilliseconds - networkHandler.HgZoneReceivedMs) / 1000f;
            float radius = HgZoneInterpolator.RadiusAt(zone, since);
            return HgZoneInterpolator.DistanceToBorder(zone, radius, entity.Pos.X, entity.Pos.Z) < 0;
        }

        // "Blue 2 · Red 1"; empty for modes without team scores. SetNewText skips unchanged text.
        private string TeamScoreLine()
        {
            var scores = networkHandler.CurrentScoreboard?.TeamScores;
            if (scores == null || scores.Length < 2) return "";
            return string.Join(" · ", scores.Select((s, i) =>
                EventManagers.eventBehavior.Shared.TeamPalette.NameOf(i) + " " + s));
        }

        // In-world team ring colours as Cairo 0..1 doubles.
        private static double[] TeamColor(int teamId)
        {
            var rgba = EventManagers.eventBehavior.Shared.TeamPalette.Rgba(teamId);
            return new double[] { rgba.X, rgba.Y, rgba.Z, rgba.W };
        }

        // Deaths change row colours, which are composed.
        private int AliveHash()
        {
            var entries = networkHandler.CurrentScoreboard?.Entries;
            if (entries == null) return 0;

            int hash = 17;
            foreach (var e in entries) hash = unchecked(hash * 31 + (e.Alive ? 1 : 0) * 7 + e.TeamId);
            return hash;
        }

        private void UpdateTexts()
        {
            var state = networkHandler.CurrentEventState;

            if (composedCountdown && Composers.ContainsKey("countdown"))
            {
                string cryKey = string.IsNullOrEmpty(state?.StartCryLangKey)
                    ? "canevents:arena-start-cry" : state.StartCryLangKey;
                string big, sub = "";
                if (state?.EventState == EventState.Running && HoldActive)
                {
                    big = Math.Max(1, (int)Math.Ceiling(holdRemaining)).ToString();
                    if (!string.IsNullOrEmpty(state.HoldLangKey)) sub = Lang.Get(state.HoldLangKey);
                }
                else if (state?.EventState == EventState.Running)
                {
                    big = Lang.Get(cryKey);
                }
                else
                {
                    int secs = (int)Math.Ceiling(countdown);
                    big = secs > 0 ? secs.ToString() : Lang.Get(cryKey);
                }
                var compo = Composers["countdown"];
                compo.GetDynamicText("text").SetNewText(big);
                compo.GetDynamicText("sub")?.SetNewText(sub);
            }

            if (composedTimer && Composers.ContainsKey("timer"))
            {
                var compo = Composers["timer"];
                compo.GetDynamicText("title").SetNewText(
                    Lang.Get("canevents:hud-event-title", (state?.GameModeCode ?? "").ToUpperInvariant()));
                compo.GetDynamicText("teams")?.SetNewText(TeamScoreLine());

                // Once per second: Statbar.SetValues has no same-value guard and its threaded recompose crashes GL if overlapped.
                int second = (int)timeRemaining;
                var bar = compo.GetStatbar("bar");
                if (bar != null && second != barSecond)
                {
                    barSecond = second;
                    bar.SetLineInterval(1);
                    bar.SetValues(maxDuration <= 0f ? 1f : timeRemaining, 0, Math.Max(1f, maxDuration));
                }

                // Open matches send no limit.
                compo.GetDynamicText("time").SetNewText(maxDuration <= 0f
                    ? "∞"
                    : string.Format("{0}:{1:D2}", (int)(timeRemaining / 60), (int)(timeRemaining % 60)));
            }

            if (composedZone && Composers.ContainsKey("zone")) UpdateZoneTexts();
            if (composedBoss && Composers.ContainsKey("boss")) UpdateBossTexts();
            if (composedScoreRows > 0 && Composers.ContainsKey("score")) UpdateScoreTexts();
            if (composedFeedRows > 0 && Composers.ContainsKey("feed")) UpdateFeedTexts();
        }

        private void UpdateZoneTexts()
        {
            var zone = networkHandler.HgZone;
            var entity = capi.World?.Player?.Entity;
            if (zone == null || entity == null) return;

            // Same math as the dome renderer.
            float since = (capi.ElapsedMilliseconds - networkHandler.HgZoneReceivedMs) / 1000f;
            float radius = HgZoneInterpolator.RadiusAt(zone, since);
            bool shrinking = HgZoneInterpolator.IsShrinking(zone);
            float secsLeft = HgZoneInterpolator.SecondsLeft(zone, since);
            double toBorder = HgZoneInterpolator.DistanceToBorder(zone, radius, entity.Pos.X, entity.Pos.Z);

            string distText = toBorder >= 0
                ? Lang.Get("canevents:hud-zone-inside", (int)toBorder)
                : Lang.Get("canevents:hud-zone-outside", (int)-toBorder);
            if (zone.AliveCount > 0)
                distText = Lang.Get("canevents:hud-zone-alive", zone.AliveCount) + "   " + distText;

            string nextText = shrinking
                ? Lang.Get("canevents:hud-zone-shrinking")
                : Lang.Get("canevents:hud-zone-shrinks-in", (int)(secsLeft / 60), (int)(secsLeft % 60));

            var compo = Composers["zone"];
            compo.GetDynamicText("dist").SetNewText(distText);
            compo.GetDynamicText("next").SetNewText(nextText);
        }

        private void UpdateScoreTexts()
        {
            var scoreboard = networkHandler.CurrentScoreboard;
            if (scoreboard?.Entries == null) return;

            var compo = Composers["score"];
            for (int i = 0; i < composedScoreRows && i < scoreboard.Entries.Length; i++)
            {
                var e = scoreboard.Entries[i];
                compo.GetDynamicText("row" + i)?.SetNewText(e.Alive ? e.PlayerName : "† " + e.PlayerName);
                compo.GetDynamicText("kills" + i)?.SetNewText(e.Kills.ToString());
            }
        }

        private void UpdateFeedTexts()
        {
            var feed = networkHandler.KillFeed;
            if (feed == null) return;

            var compo = Composers["feed"];
            int start = Math.Max(0, feed.Count - composedFeedRows);
            for (int i = 0; i < composedFeedRows && start + i < feed.Count; i++)
            {
                var entry = feed[start + i];
                compo.GetDynamicText("feed" + i)?.SetNewText(
                    entry.KillerName + "  " + entry.Method + "  " + entry.VictimName);
            }
        }
    }
}


