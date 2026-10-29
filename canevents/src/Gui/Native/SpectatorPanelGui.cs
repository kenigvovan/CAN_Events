using System;
using System.Text;
using canevents.src.EventManagers.eventBehavior.Shared;
using canevents.src.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace canevents.src.Gui.Native
{
    // Broadcaster panel. Privileged observers only: eliminated players must not see live positions.
    public class SpectatorPanelGui : HudElement
    {
        public const string NextHotkeyCode = "caneventsspectatenext";
        public const string PrevHotkeyCode = "caneventsspectateprev";

        // Older telemetry is drawn greyed.
        private const long StaleAfterMs = 3000;

        private const double PanelWidth = 260;
        private const double RowHeight = 24;
        private const double HeaderHeight = 26;
        // Two hint lines plus padding.
        private const double FooterHeight = 38;

        private readonly ClientNetworkHandler networkHandler;
        private readonly Action onStateChanged;

        // Shape of the current composition: row count and roster.
        private int composedRows = -1;
        private int composedRosterHash;
        private int composedFrameH;

        // Min gap between text redraws, well under the telemetry interval.
        private const long TextIntervalMs = 200;
        private long lastTextMs;

        public SpectatorPanelGui(ICoreClientAPI capi, ClientNetworkHandler networkHandler) : base(capi)
        {
            this.networkHandler = networkHandler;

            onStateChanged = () =>
            {
                if (!ShouldShow())
                {
                    if (IsOpened()) TryClose();
                    composedRows = -1;
                    return;
                }
                Compose();
                if (!IsOpened()) TryOpen();
            };
            networkHandler.OnSpectateStateChanged += onStateChanged;
        }

        public override void Dispose()
        {
            base.Dispose();
            networkHandler.OnSpectateStateChanged -= onStateChanged;
        }

        public override bool Focusable => false;
        public override bool ShouldReceiveKeyboardEvents() => false;

        // Hotkey handler; no-op when not observing so the keys stay free.
        public bool TryStep(int step)
        {
            if (!ShouldShow()) return false;
            networkHandler.SendFollowStep(step);
            return true;
        }

        private bool ShouldShow()
            => networkHandler.IsObserving && networkHandler.SpectatePrivileged;

        public override void OnRenderGUI(float deltaTime)
        {
            if (!ShouldShow())
            {
                if (IsOpened()) TryClose();
                return;
            }

            // Reset the throttle, or fresh empty rows blink blank for up to TextIntervalMs.
            if (ShapeChanged()) { Compose(); lastTextMs = 0; }

            // Throttled: distance changes every frame in flight, defeating SetNewText's same-string guard.
            long now = capi.ElapsedMilliseconds;
            if (now - lastTextMs >= TextIntervalMs)
            {
                lastTextMs = now;
                UpdateTexts();
            }

            base.OnRenderGUI(deltaTime);
        }

        private bool ShapeChanged()
            => RowCount() != composedRows
               || RosterHash() != composedRosterHash
               || capi.Render.FrameHeight != composedFrameH;

        private int RowCount()
        {
            var t = networkHandler.Telemetry;
            return t?.Uids?.Length ?? 0;
        }

        // Names and team colours are composed; health and distance are dynamic text.
        private int RosterHash()
        {
            var t = networkHandler.Telemetry;
            if (t?.Uids == null) return 0;

            int hash = 17;
            for (int i = 0; i < t.Uids.Length; i++)
            {
                hash = unchecked(hash * 31 + (t.Uids[i]?.GetHashCode() ?? 0));
                hash = unchecked(hash * 31 + Team(t, i));
                hash = unchecked(hash * 31 + (Alive(t, i) ? 1 : 0));
            }
            return hash;
        }

        private void Compose()
        {
            composedRows = RowCount();
            composedRosterHash = RosterHash();
            composedFrameH = capi.Render.FrameHeight;

            Composers.ClearComposers();

            var telemetry = networkHandler.Telemetry;
            double height = HeaderHeight + composedRows * RowHeight + FooterHeight;
            double screenH = capi.Render.FrameHeight / RuntimeEnv.GUIScale;

            // Left edge, centred: vanilla HUD owns the top and bottom-left.
            var bounds = ElementBounds.Fixed(10, Math.Max(10, screenH / 2 - height / 2), PanelWidth, height);
            var bg = ElementBounds.Fixed(0, 0, PanelWidth, height);

            var composer = capi.Gui
                .CreateCompo("canevents-spectator-panel", bounds)
                .AddGameOverlay(bg, Palette.HudBg)
                .BeginChildElements(bg)
                    .AddStaticText(Lang.Get("canevents:spectate-panel-title"),
                        CairoFont.WhiteSmallText().WithColor(Palette.Title),
                        ElementBounds.Fixed(10, 5, PanelWidth - 20, 18));

            for (int i = 0; i < composedRows; i++)
            {
                double y = HeaderHeight + i * RowHeight;
                bool alive = Alive(telemetry, i);
                var font = CairoFont.WhiteSmallText()
                    .WithColor(alive ? TeamColor(Team(telemetry, i)) : Palette.Idle);

                // Text, not buttons: an unfocused HUD never frees the cursor, so buttons can't be clicked.
                composer
                    .AddStaticText(RowLabel(telemetry, i), font,
                        ElementBounds.Fixed(10, y + 3, PanelWidth - 100, 18))
                    .AddDynamicText("", font.Clone().WithOrientation(EnumTextOrientation.Right),
                        ElementBounds.Fixed(PanelWidth - 88, y + 3, 80, 18), "info" + i);
            }

            double footerY = HeaderHeight + composedRows * RowHeight;
            var hintFont = CairoFont.WhiteSmallText().WithColor(Palette.Detail);

            composer
                .AddStaticText(Lang.Get("canevents:spectate-panel-hint"), hintFont,
                    ElementBounds.Fixed(10, footerY, PanelWidth - 20, 18))
                .AddStaticText(Lang.Get("canevents:spectate-panel-leave"), hintFont,
                    ElementBounds.Fixed(10, footerY + 16, PanelWidth - 20, 18));

            Composers["spectator"] = composer.EndChildElements().Compose();
        }

        private void UpdateTexts()
        {
            var telemetry = networkHandler.Telemetry;
            if (telemetry?.Uids == null || !Composers.ContainsKey("spectator")) return;

            bool stale = capi.ElapsedMilliseconds - networkHandler.TelemetryReceivedMs > StaleAfterMs;
            var self = capi.World?.Player?.Entity?.Pos;
            var compo = Composers["spectator"];

            for (int i = 0; i < composedRows && i < telemetry.Uids.Length; i++)
            {
                var text = compo.GetDynamicText("info" + i);
                if (text == null) continue;

                if (stale)
                {
                    text.SetNewText(Lang.Get("canevents:spectate-panel-stale"));
                    continue;
                }

                var sb = new StringBuilder();
                float hp = At(telemetry.Health, i);
                float max = At(telemetry.MaxHealth, i);
                if (max > 0) sb.Append((int)Math.Ceiling(hp)).Append('/').Append((int)max);

                if (self != null)
                {
                    double dx = At(telemetry.X, i) - self.X;
                    double dy = At(telemetry.Y, i) - self.Y;
                    double dz = At(telemetry.Z, i) - self.Z;
                    if (sb.Length > 0) sb.Append("  ");
                    sb.Append((int)Math.Sqrt(dx * dx + dy * dy + dz * dz)).Append('m');
                }

                text.SetNewText(sb.ToString());
            }
        }

        private static string RowLabel(SpectatorTelemetryMsg t, int i)
        {
            string name = t.Names != null && i < t.Names.Length ? t.Names[i] : "?";
            // Same dead marker as the scoreboard.
            if (!Alive(t, i)) return "† " + name;
            // Spelled out: the GUI font lacks most symbol glyphs.
            if (t.IsBot != null && i < t.IsBot.Length && t.IsBot[i]) return name + " (bot)";
            return name;
        }

        private static bool Alive(SpectatorTelemetryMsg t, int i)
            => t?.Alive != null && i < t.Alive.Length && t.Alive[i];

        private static int Team(SpectatorTelemetryMsg t, int i)
            => t?.TeamIds != null && i < t.TeamIds.Length ? t.TeamIds[i] : -1;

        private static double At(double[] values, int i)
            => values != null && i < values.Length ? values[i] : 0;

        private static float At(float[] values, int i)
            => values != null && i < values.Length ? values[i] : 0f;

        // Must match the scoreboard and in-world team rings.
        private static double[] TeamColor(int teamId)
        {
            var rgba = TeamPalette.Rgba(teamId);
            return new double[] { rgba.X, rgba.Y, rgba.Z, rgba.W };
        }
    }
}
