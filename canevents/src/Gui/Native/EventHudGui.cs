using System;
using System.Text;
using canevents.src.EventManagers;
using canevents.src.Gui;
using canevents.src.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace canevents.src.Gui.Native
{
    /// <summary>
    /// The in-match HUD: match timer, storm readout, scoreboard and kill feed. One composer per
    /// panel, since a composer is the unit that owns a position and each is draggable (see
    /// <see cref="HudLayout"/>). Composition happens when the SHAPE changes; the numbers inside are
    /// dynamic texts refreshed per frame.
    /// </summary>
    public class EventHudGui : HudElement
    {
        private readonly ClientNetworkHandler networkHandler;
        private readonly HudLayout layout;

        // Local countdown, ticked between packets so the numbers move every frame.
        private float timeRemaining, maxDuration, countdown;
        private long lastEventId;

        // What the current composition was built for: when any of these change, the shape changed.
        private int composedScoreRows = -1;
        private int composedFeedRows = -1;
        private bool composedZone, composedTimer, composedCountdown;
        private int composedFrameW, composedFrameH;

        // Colour states. A colour lives in the composed font/statbar, so a colour change is a shape change.
        private bool composedUrgent;    // the clock is nearly out
        private bool composedOutside;   // the player is outside the storm ring
        private int composedAliveHash = -1;   // who is still alive, and on which team

        /// <summary>Below this fraction of the match the timer turns red.</summary>
        private const float UrgentFraction = 0.15f;

        private const int MaxFeedRows = 5;

        private readonly Action onStateUpdated;

        public EventHudGui(ICoreClientAPI capi, ClientNetworkHandler networkHandler, HudLayout layout) : base(capi)
        {
            this.networkHandler = networkHandler;
            this.layout = layout;

            // A closed dialog gets no render calls, so opening cannot be decided in OnRenderGUI; the
            // state packet is what says a match exists.
            onStateUpdated = () =>
            {
                if (networkHandler.CurrentEventState == null)
                {
                    if (IsOpened()) TryClose();
                    return;
                }
                if (!IsOpened()) { Compose(); TryOpen(); }
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

        /// <summary>Rebuild after the layout editor moved something.</summary>
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

            if (state.EventState == EventState.LocationFound)
            {
                countdown = Math.Max(0f, countdown - deltaTime);
            }
            else if (state.EventState == EventState.Running)
            {
                timeRemaining = Math.Max(0f, timeRemaining - deltaTime);
            }

            if (ShapeChanged()) Compose();
            UpdateTexts();

            base.OnRenderGUI(deltaTime);
        }

        private bool ShapeChanged()
        {
            var state = networkHandler.CurrentEventState;
            bool wantCountdown = state?.EventState == EventState.LocationFound;
            bool wantTimer = state?.EventState != EventState.LocationFound;
            bool wantZone = networkHandler.HgZone?.Active == true;
            int scoreRows = networkHandler.CurrentScoreboard?.Entries?.Length ?? 0;
            int feedRows = Math.Min(MaxFeedRows, networkHandler.KillFeed?.Count ?? 0);

            return wantCountdown != composedCountdown
                || wantTimer != composedTimer
                || wantZone != composedZone
                || scoreRows != composedScoreRows
                || feedRows != composedFeedRows
                || IsUrgent() != composedUrgent
                || IsOutside() != composedOutside
                || AliveHash() != composedAliveHash
                || capi.Render.FrameWidth != composedFrameW
                || capi.Render.FrameHeight != composedFrameH;
        }

        // Composition

        private void Compose()
        {
            var state = networkHandler.CurrentEventState;
            composedCountdown = state?.EventState == EventState.LocationFound;
            composedTimer = state?.EventState != EventState.LocationFound;
            composedZone = networkHandler.HgZone?.Active == true;
            composedScoreRows = networkHandler.CurrentScoreboard?.Entries?.Length ?? 0;
            composedFeedRows = Math.Min(MaxFeedRows, networkHandler.KillFeed?.Count ?? 0);
            composedUrgent = IsUrgent();
            composedOutside = IsOutside();
            composedAliveHash = AliveHash();
            composedFrameW = capi.Render.FrameWidth;
            composedFrameH = capi.Render.FrameHeight;

            Composers.ClearComposers();

            if (composedCountdown) ComposeCountdown();
            if (composedTimer) ComposeTimer();
            if (composedZone) ComposeZone();
            if (composedScoreRows > 0) ComposeScoreboard();
            if (composedFeedRows > 0) ComposeKillFeed();
        }

        /// <summary>
        /// Screen position for a panel: the player's stored spot, or the default. Positions are
        /// normalised so a layout survives a resolution change; GUI coords are unscaled, hence the
        /// divide by GUIScale.
        /// </summary>
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

            // The clock carries the colour: brass, red once time is nearly out.
            var titleFont = CairoFont.WhiteSmallText().WithColor(Palette.Header);
            var timeFont = CairoFont.WhiteSmallishText()
                .WithColor(composedUrgent ? Palette.Stop : Palette.Title)
                .WithOrientation(EnumTextOrientation.Center);

            Composers["timer"] = capi.Gui
                .CreateCompo("canevents-hud-timer", bounds)
                .AddGameOverlay(bg, Palette.HudBg)
                .BeginChildElements(bg)
                    // Three stacked rows, not overlapping: the clock and the bar used to share a y
                    // and the digits were printed over the fill.
                    .AddDynamicText("", titleFont, ElementBounds.Fixed(10, 4, w - 20, 16), "title")
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

            // Outside the ring the distance turns red - the storm is already hurting.
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
                // Team colour while alive, grey once eliminated - the palette the in-world rings use.
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

            // Oldest entries fade out so the newest kill stands out.
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

        // The pre-match countdown: big, centred, no background - read while looking at the arena.
        private void ComposeCountdown()
        {
            double screenW = capi.Render.FrameWidth / RuntimeEnv.GUIScale;
            double screenH = capi.Render.FrameHeight / RuntimeEnv.GUIScale;
            var bounds = ElementBounds.Fixed(screenW / 2 - 200, screenH / 3 - 60, 400, 120);

            var font = CairoFont.WhiteSmallText()
                .WithFontSize(90)
                .WithWeight(Cairo.FontWeight.Bold)
                .WithColor(Palette.Title)
                .WithStroke(new double[] { 0, 0, 0, 1 }, 3);

            Composers["countdown"] = capi.Gui
                .CreateCompo("canevents-hud-countdown", bounds)
                .AddDynamicText("", font, EnumTextOrientation.Center, ElementBounds.Fixed(0, 0, 400, 120), "text")
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

        // The same colours the in-world team rings use, converted to Cairo's 0..1 doubles.
        private static double[] TeamColor(int teamId)
        {
            var rgba = EventManagers.eventBehavior.Shared.TeamPalette.Rgba(teamId);
            return new double[] { rgba.X, rgba.Y, rgba.Z, rgba.W };
        }

        // A player dying changes a row's colour, and colours live in the composition.
        private int AliveHash()
        {
            var entries = networkHandler.CurrentScoreboard?.Entries;
            if (entries == null) return 0;

            int hash = 17;
            foreach (var e in entries) hash = unchecked(hash * 31 + (e.Alive ? 1 : 0) * 7 + e.TeamId);
            return hash;
        }

        // Per-frame text

        private void UpdateTexts()
        {
            var state = networkHandler.CurrentEventState;

            if (composedCountdown && Composers.ContainsKey("countdown"))
            {
                int secs = (int)Math.Ceiling(countdown);
                string cryKey = string.IsNullOrEmpty(state?.StartCryLangKey)
                    ? "canevents:arena-start-cry" : state.StartCryLangKey;
                Composers["countdown"].GetDynamicText("text")
                    .SetNewText(secs > 0 ? secs.ToString() : Lang.Get(cryKey));
            }

            if (composedTimer && Composers.ContainsKey("timer"))
            {
                var compo = Composers["timer"];
                compo.GetDynamicText("title").SetNewText(
                    Lang.Get("canevents:hud-event-title", (state?.GameModeCode ?? "").ToUpperInvariant()));

                var bar = compo.GetStatbar("bar");
                if (bar != null)
                {
                    bar.SetLineInterval(1);
                    bar.SetValues(timeRemaining, 0, Math.Max(1f, maxDuration));
                }

                compo.GetDynamicText("time").SetNewText(
                    string.Format("{0}:{1:D2}", (int)(timeRemaining / 60), (int)(timeRemaining % 60)));
            }

            if (composedZone && Composers.ContainsKey("zone")) UpdateZoneTexts();
            if (composedScoreRows > 0 && Composers.ContainsKey("score")) UpdateScoreTexts();
            if (composedFeedRows > 0 && Composers.ContainsKey("feed")) UpdateFeedTexts();
        }

        private void UpdateZoneTexts()
        {
            var zone = networkHandler.HgZone;
            var entity = capi.World?.Player?.Entity;
            if (zone == null || entity == null) return;

            // Shared with the dome renderer so the drawn ring and the printed distance agree.
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


