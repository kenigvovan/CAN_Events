using System;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace canevents.src.EventManagers.eventBehavior.Shared
{
    // Draws zone boundaries with particles. Server-side SpawnParticles only reaches
    // clients in range, so an arena far from spawn costs nothing to bystanders.
    public static class ZoneVisualizer
    {
        private static readonly int NeutralColor = ColorUtil.ToRgba(255, 180, 180, 180);
        private static readonly int ContestedColor = ColorUtil.ToRgba(255, 60, 60, 255);

        // Frosty light-blue: the HG storm deals Frost damage, so the dome reads as cold rather than as
        // a team's claim. Alpha keeps the world visible through the border.
        public static readonly int StormColor = ColorUtil.ToRgba(170, 255, 210, 160);

        // ownerTeam: >=0 owning team, KingOfTheHillEB.OwnerNeutral, or OwnerContested.
        public static int OwnerColor(int ownerTeam)
        {
            if (ownerTeam == KingOfTheHillEB.OwnerContested) return ContestedColor;
            if (ownerTeam < 0) return NeutralColor;
            // Same palette the HUD scoreboard uses.
            return TeamPalette.Packed(ownerTeam);
        }

        /// <summary>
        /// Vertical particle wall: DrawRing repeated at 1-block steps upward. baseY is the floor
        /// SURFACE (blockY + 1), else the bottom ring sits inside the floor block and is hidden.
        /// </summary>
        public static void DrawWall(ICoreServerAPI sapi, double cx, double baseY, double cz,
                                    float radius, int segments, int color, int height)
        {
            for (int k = 0; k < height; k++)
                DrawRing(sapi, cx, baseY + k, cz, radius, segments, color);
        }

        /// <summary>
        /// Hemispherical dome over the zone: latitude rings up to a single cap particle, so the border
        /// reads as a closed sphere instead of a wall with open sky above it. baseY is the floor
        /// SURFACE, like DrawWall; segment count follows each ring's radius to keep density even.
        /// </summary>
        public static void DrawDome(ICoreServerAPI sapi, double cx, double baseY, double cz,
                                    float radius, int color)
        {
            if (sapi == null || radius <= 0f) return;

            var props = MakeStormProps(color);

            const int bands = 5; // 0°..72°; the 90° cap is the single particle below
            for (int b = 0; b < bands; b++)
            {
                double lat = b * (Math.PI / 2.0) / bands;
                float ringR = radius * (float)Math.Cos(lat);
                double y = baseY + radius * Math.Sin(lat);
                // Hard cap per ring: every particle is its own SpawnParticles packet, and a dense dome
                // redrawn each second exhausts the client particle pool and crashes ParticlePoolQuads.
                int segments = Math.Min(24, Math.Max(6, (int)(ringR * 0.6f)));
                if (ringR <= 0f) continue;
                for (int i = 0; i < segments; i++)
                {
                    double angle = i * 2.0 * Math.PI / segments;
                    props.MinPos = new Vec3d(cx + Math.Cos(angle) * ringR, y, cz + Math.Sin(angle) * ringR);
                    sapi.World.SpawnParticles(props);
                }
            }

            props.MinPos = new Vec3d(cx, baseY + radius, cz);
            sapi.World.SpawnParticles(props);
        }

        // Storm-border look: big soft quads drifting upward, so the dome reads as weather rather than
        // debug markers. Fresh instance per call, because DrawDome mutates MinPos per particle.
        private static SimpleParticleProperties MakeStormProps(int color)
        {
            var props = MakeProps(color);
            props.MinSize = 0.5f;
            props.MaxSize = 0.9f;
            props.MinVelocity = new Vec3f(-0.05f, 0.05f, -0.05f);
            props.AddVelocity = new Vec3f(0.1f, 0.25f, 0.1f);
            props.LifeLength = 1.6f; // drifts need a bit longer than the static rings to read as flow
            props.VertexFlags = 120; // mild glow: visible at night without lighting the arena
            return props;
        }

        private static SimpleParticleProperties MakeProps(int color)
        {
            return new SimpleParticleProperties(
                1, 1,
                color,
                new Vec3d(), new Vec3d(),
                new Vec3f(), new Vec3f(),
                // Outlive the caller's 1s cadence so the ring never blinks between ticks.
                1.2f,
                0f,
                0.3f, 0.3f,
                EnumParticleModel.Quad);
        }

        public static void DrawRing(ICoreServerAPI sapi, double cx, double cy, double cz,
                                    float radius, int segments, int color)
        {
            if (sapi == null || segments < 3 || radius <= 0f) return;

            var props = MakeProps(color);

            for (int i = 0; i < segments; i++)
            {
                double angle = i * 2.0 * Math.PI / segments;
                props.MinPos = new Vec3d(
                    cx + Math.Cos(angle) * radius,
                    cy + 0.2,
                    cz + Math.Sin(angle) * radius);
                sapi.World.SpawnParticles(props);
            }
        }
    }
}
