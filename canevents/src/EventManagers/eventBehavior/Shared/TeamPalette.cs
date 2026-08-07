using System;
using System.Numerics;
using Vintagestory.API.MathTools;

namespace canevents.src.EventManagers.eventBehavior.Shared
{
    /// <summary>
    /// The single team colour palette, in both forms the mod needs: packed RGBA for in-world
    /// particles and a float vector for the ImGui HUD. Kept in one place so the zone ring and the
    /// scoreboard can never disagree about who owns what.
    /// </summary>
    public static class TeamPalette
    {
        // Blue, red, green, yellow, orange, purple: distinguishable at a glance and from neutral grey.
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

        /// <summary>HUD colour for a team. Out-of-range ids wrap, negative ones read as white.</summary>
        public static Vector4 Rgba(int teamId)
        {
            if (teamId < 0) return new Vector4(1f, 1f, 1f, 1f);
            var (r, g, b) = Teams[teamId % Teams.Length];
            return new Vector4(r, g, b, 1f);
        }

        /// <summary>Particle colour for a team, in the engine's packed form.</summary>
        public static int Packed(int teamId)
        {
            if (teamId < 0) return ColorUtil.ToRgba(255, 255, 255, 255);
            var (r, g, b) = Teams[teamId % Teams.Length];
            return ColorUtil.ToRgba(255, Byte(b), Byte(g), Byte(r));
        }

        private static int Byte(float c) => (int)Math.Round(Math.Min(1f, Math.Max(0f, c)) * 255f);
    }
}
