using System;
using System.Numerics;
using Vintagestory.API.MathTools;

namespace canevents.src.EventManagers.eventBehavior.Shared
{
    // Team colours for both particles (packed) and the HUD (Vector4).
    public static class TeamPalette
    {
        // Blue, red, green, yellow, orange, purple.
        private static readonly (float r, float g, float b)[] Teams =
        {
            (0.2f, 0.6f, 1.0f),
            (1.0f, 0.3f, 0.3f),
            (0.3f, 1.0f, 0.3f),
            (1.0f, 1.0f, 0.3f),
            (1.0f, 0.5f, 0.0f),
            (0.8f, 0.3f, 1.0f)
        };

        public static int Count => Teams.Length;

        // Ids wrap; negative = white.
        public static Vector4 Rgba(int teamId)
        {
            if (teamId < 0) return new Vector4(1f, 1f, 1f, 1f);
            var (r, g, b) = Teams[teamId % Teams.Length];
            return new Vector4(r, g, b, 1f);
        }

        // Particle colours are packed BGR.
        public static int Packed(int teamId)
        {
            if (teamId < 0) return ColorUtil.ToRgba(255, 255, 255, 255);
            var (r, g, b) = Teams[teamId % Teams.Length];
            return ColorUtil.ToRgba(255, Byte(b), Byte(g), Byte(r));
        }

        private static int Byte(float c) => (int)Math.Round(Math.Min(1f, Math.Max(0f, c)) * 255f);

        // Same order as Teams; used for typed team args, so English and stable.
        private static readonly string[] Codes = { "blue", "red", "green", "yellow", "orange", "purple" };

        public static string NameOf(int teamId)
            => teamId < 0
                ? "?"
                : Vintagestory.API.Config.Lang.Get("canevents:team-color-" + Codes[teamId % Codes.Length])
                  + (teamId >= Codes.Length ? " " + (teamId + 1) : "");

        // "red", "2" (1-based) -> index; -1 if neither.
        public static int Parse(string arg)
        {
            if (string.IsNullOrWhiteSpace(arg)) return -1;
            arg = arg.Trim().ToLowerInvariant();
            if (int.TryParse(arg, out int n)) return n >= 1 ? n - 1 : -1;
            return Array.IndexOf(Codes, arg);
        }
    }
}
