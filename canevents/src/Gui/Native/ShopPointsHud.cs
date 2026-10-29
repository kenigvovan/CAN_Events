using System;
using System.Collections.Generic;
using canevents.src.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace canevents.src.Gui.Native
{
    // Match shop points, bottom-right unless moved with /canhud. Every gain pops up as "+N" that jumps, floats up and
    // fades, and the counter rolls up to the new total instead of jumping. The pop-ups are drawn as text
    // textures straight from OnRenderGUI, so the animation never recomposes anything.
    public class ShopPointsHud : HudElement
    {
        // Shared with the layout editor's ghost.
        public const double Width = 200, Height = 46;
        public static double DefaultX(double screenW) => screenW - Width - 10;
        public static double DefaultY(double screenH) => screenH - Height - 60;

        private const float PopSeconds = 1.6f;
        private const float RollSeconds = 0.6f;
        private const float RisePx = 52;

        private sealed class Pop
        {
            public LoadedTexture Texture;
            public float Age;
            // -1..1, so gains in a quick row don't stack on one spot.
            public float Drift;
        }

        private readonly ClientNetworkHandler networkHandler;
        private readonly HudLayout layout;
        private readonly List<Pop> pops = new List<Pop>();
        private readonly CairoFont popFont;
        private readonly Random rand = new Random();
        private readonly Action onChanged;
        private readonly Action<int> onGained;

        private int target;
        private float rollFrom;
        private float rollAge = RollSeconds;
        private int shownValue = -1;
        private int composedFrameW, composedFrameH;

        public ShopPointsHud(ICoreClientAPI capi, ClientNetworkHandler networkHandler, HudLayout layout) : base(capi)
        {
            this.networkHandler = networkHandler;
            this.layout = layout;
            popFont = CairoFont.WhiteMediumText()
                .WithFontSize(30)
                .WithWeight(Cairo.FontWeight.Bold)
                .WithColor(Palette.Go)
                .WithStroke(new double[] { 0, 0, 0, 1 }, 2.5);

            onChanged = OnShopChanged;
            onGained = OnGained;
            networkHandler.OnShopChanged += onChanged;
            networkHandler.OnShopPointsGained += onGained;
        }

        public override void Dispose()
        {
            base.Dispose();
            networkHandler.OnShopChanged -= onChanged;
            networkHandler.OnShopPointsGained -= onGained;
            ClearPops();
        }

        public override bool Focusable => false;
        public override bool ShouldReceiveKeyboardEvents() => false;

        private void OnShopChanged()
        {
            var shop = networkHandler.Shop;
            if (shop == null)
            {
                ClearPops();
                if (IsOpened()) TryClose();
                return;
            }

            if (!IsOpened())
            {
                // A new match starts from its own total, no roll from the last one.
                target = shop.Points;
                rollAge = RollSeconds;
                Compose();
                TryOpen();
                return;
            }

            if (shop.Points == target) return;
            rollFrom = Displayed();
            rollAge = 0f;
            target = shop.Points;
        }

        private void OnGained(int points)
        {
            pops.Add(new Pop
            {
                Texture = capi.Gui.TextTexture.GenTextTexture("+" + points, popFont),
                Drift = (float)(rand.NextDouble() * 2 - 1)
            });
            capi.Gui.PlaySound("menubutton_press");
        }

        private float Displayed()
        {
            if (rollAge >= RollSeconds) return target;
            float t = rollAge / RollSeconds;
            return rollFrom + (target - rollFrom) * (1 - (1 - t) * (1 - t));
        }

        public void OnLayoutChanged()
        {
            if (IsOpened()) Compose();
        }

        // Stored positions are normalised and clamped like EventHudGui's panels.
        private ElementBounds PanelBounds()
        {
            double screenW = capi.Render.FrameWidth / RuntimeEnv.GUIScale;
            double screenH = capi.Render.FrameHeight / RuntimeEnv.GUIScale;
            var pos = layout?.ShopPoints;
            double x = pos != null && pos.IsSet ? pos.X * screenW : DefaultX(screenW);
            double y = pos != null && pos.IsSet ? pos.Y * screenH : DefaultY(screenH);
            x = GameMath.Clamp(x, 0, Math.Max(0, screenW - Width));
            y = GameMath.Clamp(y, 0, Math.Max(0, screenH - Height));
            return ElementBounds.Fixed(x, y, Width, Height);
        }

        private void Compose()
        {
            composedFrameW = capi.Render.FrameWidth;
            composedFrameH = capi.Render.FrameHeight;
            shownValue = -1;

            var bg = ElementBounds.Fixed(0, 0, Width, Height);
            var labelFont = CairoFont.WhiteSmallText().WithColor(Palette.Header);
            var valueFont = CairoFont.WhiteSmallishText()
                .WithWeight(Cairo.FontWeight.Bold)
                .WithColor(Palette.Title)
                .WithOrientation(EnumTextOrientation.Right);
            var hintFont = CairoFont.WhiteDetailText().WithColor(Palette.Detail);

            SingleComposer = capi.Gui
                .CreateCompo("canevents-hud-shoppoints", PanelBounds())
                .AddGameOverlay(bg, Palette.HudBg)
                .BeginChildElements(bg)
                    .AddStaticText(Lang.Get("canevents:hud-shop-points"), labelFont, ElementBounds.Fixed(10, 5, 110, 18))
                    .AddDynamicText("", valueFont, ElementBounds.Fixed(100, 2, Width - 110, 22), "value")
                    .AddDynamicText("", hintFont, ElementBounds.Fixed(10, 26, Width - 20, 16), "hint")
                .EndChildElements()
                .Compose();
        }

        public override void OnRenderGUI(float deltaTime)
        {
            if (networkHandler.Shop == null) return;
            if (capi.Render.FrameWidth != composedFrameW || capi.Render.FrameHeight != composedFrameH) Compose();

            if (rollAge < RollSeconds) rollAge += deltaTime;
            int value = (int)Math.Round(Displayed());
            if (value != shownValue)
            {
                shownValue = value;
                SingleComposer?.GetDynamicText("value")?.SetNewText(value.ToString());
            }
            SingleComposer?.GetDynamicText("hint")?.SetNewText(
                Lang.Get("canevents:hud-shop-hint", MatchShopGui.HotkeyName(capi)));

            base.OnRenderGUI(deltaTime);
            RenderPops(deltaTime);
        }

        private void RenderPops(float deltaTime)
        {
            if (pops.Count == 0 || SingleComposer == null) return;

            var panel = SingleComposer.Bounds;
            double right = panel.renderX + panel.OuterWidth;
            double top = panel.renderY;

            for (int i = pops.Count - 1; i >= 0; i--)
            {
                var pop = pops[i];
                pop.Age += deltaTime;
                if (pop.Age >= PopSeconds)
                {
                    pop.Texture.Dispose();
                    pops.RemoveAt(i);
                    continue;
                }

                float t = pop.Age / PopSeconds;
                float rise = 1 - (1 - t) * (1 - t) * (1 - t);
                // Overshoots to 1.4 and settles, the "jump" of a fresh gain.
                float scale = t < 0.18f ? 1f + 0.4f * (float)Math.Sin(t / 0.18f * Math.PI) : 1f;
                float alpha = t < 0.08f ? t / 0.08f : t > 0.55f ? 1 - (t - 0.55f) / 0.45f : 1f;

                float w = pop.Texture.Width * scale;
                float h = pop.Texture.Height * scale;
                double x = right - pop.Texture.Width - GuiElement.scaled(14) + pop.Drift * GuiElement.scaled(14)
                           - (w - pop.Texture.Width) / 2;
                double y = top - pop.Texture.Height - GuiElement.scaled(RisePx) * rise - (h - pop.Texture.Height) / 2;

                // Premultiplied texture: every channel fades together.
                capi.Render.Render2DTexturePremultipliedAlpha(pop.Texture.TextureId, x, y, w, h, 60f,
                    new Vec4f(alpha, alpha, alpha, alpha));
            }
        }

        private void ClearPops()
        {
            foreach (var pop in pops) pop.Texture?.Dispose();
            pops.Clear();
        }
    }
}
