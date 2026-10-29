using System.Numerics;
using Vintagestory.API.Client;

namespace canevents.src.Gui
{
    // Client-only. Positions are 0..1 of the viewport.
    public class HudLayout
    {
        // Never moved, use the default spot.
        public const float Unset = -1f;

        public class PanelPos
        {
            public float X = Unset;
            public float Y = Unset;

            public bool IsSet => X >= 0f && Y >= 0f;
            public void Clear() { X = Unset; Y = Unset; }
        }

        public PanelPos Timer { get; set; } = new PanelPos();
        public PanelPos Zone { get; set; } = new PanelPos();
        public PanelPos Scoreboard { get; set; } = new PanelPos();
        public PanelPos KillFeed { get; set; } = new PanelPos();
        // "Registration is open" toast.
        public PanelPos Invite { get; set; } = new PanelPos();
        // Match shop points.
        public PanelPos ShopPoints { get; set; } = new PanelPos();

        public enum InviteDisplay { Full, Mini, Off }

        // Stored as a string so the json stays hand-editable.
        [Newtonsoft.Json.JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
        public InviteDisplay InviteMode { get; set; } = InviteDisplay.Full;

        // Old or hand-edited json can leave these null.
        public void EnsureComplete()
        {
            Timer ??= new PanelPos();
            Zone ??= new PanelPos();
            Scoreboard ??= new PanelPos();
            KillFeed ??= new PanelPos();
            Invite ??= new PanelPos();
            ShopPoints ??= new PanelPos();
        }

        public void ResetAll()
        {
            Timer.Clear();
            Zone.Clear();
            Scoreboard.Clear();
            KillFeed.Clear();
            Invite.Clear();
            ShopPoints.Clear();
            InviteMode = InviteDisplay.Full;
        }

        // Clamped to the screen in case the resolution changed.
        public static Vector2 Resolve(PanelPos pos, Vector2 viewport, Vector2 size, Vector2 fallback)
        {
            var wanted = pos != null && pos.IsSet
                ? new Vector2(pos.X * viewport.X, pos.Y * viewport.Y)
                : fallback;

            float maxX = System.Math.Max(0f, viewport.X - size.X);
            float maxY = System.Math.Max(0f, viewport.Y - size.Y);
            return new Vector2(
                System.Math.Clamp(wanted.X, 0f, maxX),
                System.Math.Clamp(wanted.Y, 0f, maxY));
        }

        public static void Store(PanelPos pos, Vector2 viewport, Vector2 topLeft)
        {
            if (pos == null || viewport.X <= 0f || viewport.Y <= 0f) return;
            pos.X = topLeft.X / viewport.X;
            pos.Y = topLeft.Y / viewport.Y;
        }

        private const string FileName = "canevents-hud.json";

        public static HudLayout Load(ICoreClientAPI capi)
        {
            HudLayout loaded = null;
            try { loaded = capi.LoadModConfig<HudLayout>(FileName); }
            catch (System.Exception e)
            {
                capi.Logger.Warning("[canevents] Could not read {0}, using default HUD layout: {1}", FileName, e.Message);
            }
            loaded ??= new HudLayout();
            loaded.EnsureComplete();
            return loaded;
        }

        public void Save(ICoreClientAPI capi)
        {
            try { capi.StoreModConfig(this, FileName); }
            catch (System.Exception e)
            {
                capi.Logger.Warning("[canevents] Could not save {0}: {1}", FileName, e.Message);
            }
        }
    }
}
