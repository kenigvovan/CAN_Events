using System;
using System.Text;
using canevents.src.EventManagers.eventBehavior.Shared;
using canevents.src.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace canevents.src.Gui.Native
{
    /// <summary>
    /// The broadcaster panel: who is on the field, how much health they have left, how far away they
    /// are, and a button per fighter that puts the camera on them. Only drawn for an observer the
    /// server told us is privileged - an eliminated player watching from the spectator seat gets the
    /// ordinary match HUD and nothing more, because live positions of the people still fighting is
    /// exactly the information their team would want them to leak.
    ///
    /// The rows are real buttons rather than a drawn list: clicking a name is the fastest way to
    /// follow the fight, and the two hotkeys only cover "next" and "previous".
    /// </summary>
    public class SpectatorPanelGui : HudElement
    {
        public const string NextHotkeyCode = "caneventsspectatenext";
        public const string PrevHotkeyCode = "caneventsspectateprev";

        /// <summary>Telemetry older than this is drawn greyed: the feed stopped, so the positions on
        /// screen are no longer where anyone is.</summary>
        private const long StaleAfterMs = 3000;

        private const double PanelWidth = 260;
        private const double RowHeight = 24;
        private const double HeaderHeight = 26;
        // Two hint lines plus a little breathing room below the last one.
        private const double FooterHeight = 38;

        private readonly ClientNetworkHandler networkHandler;
        private readonly Action onStateChanged;

        // What the current composition was built for. A row is a button, so the row COUNT and who is
        // in each row are both part of the shape.
        private int composedRows = -1;
        private int composedRosterHash;
        private int composedFrameH;

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

        /// <summary>Steps the camera one fighter along. Wired to the two hotkeys; a no-op when not
        /// observing, so the keys stay free for everyone else.</summary>
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

            if (ShapeChanged()) Compose();
            UpdateTexts();

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

        // Names and team colours live in the composition, so a roster change is a shape change.
        // Health and position do not - they are per-frame text.
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

            // Left edge, vertically centred: the ordinary HUD owns the top and the bottom-left, and
            // this panel has to coexist with all of it.
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

                // Text, not buttons. A HUD panel is not focusable - it is on screen for the whole
                // match and grabbing the mouse would take away the flying it exists to support - and
                // an unfocused dialog never frees the cursor, so a button here can be drawn but never
                // clicked. The camera moves on the hotkeys instead.
                //
                // The name is composed (its colour is), the readout beside it is dynamic: health and
                // distance change with every packet, and recomposing that often would flicker.
                composer
                    .AddStaticText(RowLabel(telemetry, i), font,
                        ElementBounds.Fixed(10, y + 3, PanelWidth - 100, 18))
                    .AddDynamicText("", font.Clone().WithOrientation(EnumTextOrientation.Right),
                        ElementBounds.Fixed(PanelWidth - 88, y + 3, 80, 18), "info" + i);
            }

            // Two lines: how to move the camera, and how to get out. The way out belongs on screen -
            // a spectator with no idea how to stop being one is stuck looking for the answer in chat.
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
            // The dagger is the same marker the scoreboard uses for a dead player.
            if (!Alive(t, i)) return "† " + name;
            // A bot name reads like a player name on the roster; the observer is the one person who
            // needs to know which is which. Spelled out rather than a symbol: the GUI font has no
            // glyph for most of them and draws an empty box instead.
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

        // The same colours the scoreboard and the in-world team rings use.
        private static double[] TeamColor(int teamId)
        {
            var rgba = TeamPalette.Rgba(teamId);
            return new double[] { rgba.X, rgba.Y, rgba.Z, rgba.W };
        }
    }
}
