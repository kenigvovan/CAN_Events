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

        /// <summary>Arena footprint: warm off-white, so it never reads as a team's claim or as the
        /// cold storm border.</summary>
        public static readonly int ArenaBorderColor = ColorUtil.ToRgba(120, 190, 215, 240);

        /// <summary>
        /// Outlines an arena's footprint - the edge of the playable area for every mode that has no
        /// shrinking border of its own. Most matches drew nothing at all before this, so "how far can
        /// I go" was something players learned by walking out of the fight.
        ///
        /// Particle budget matters more than density here: an arena can be hundreds of blocks across,
        /// and one SpawnParticles call per particle redrawn every second will exhaust the client pool
        /// (the same trap documented on <see cref="DrawDome"/>). The step is therefore derived from
        /// the perimeter, never the other way round.
        /// </summary>
        public static void DrawBox(ICoreServerAPI sapi, BlockPos min, BlockPos max, int color,
                                   int maxParticles = 48)
        {
            if (sapi == null || min == null || max == null || maxParticles < 4) return;

            double x1 = Math.Min(min.X, max.X), x2 = Math.Max(min.X, max.X) + 1;
            double z1 = Math.Min(min.Z, max.Z), z2 = Math.Max(min.Z, max.Z) + 1;
            // Top of the footprint plus a little: at floor level the markers sink into the ground.
            double y = Math.Max(min.Y, max.Y) + 1.2;

            double width = x2 - x1, depth = z2 - z1;
            if (width <= 0 || depth <= 0) return;

            // Points are dealt to the two axes in proportion to their length, so a long thin arena
            // does not end up with the same number on a 10-block side as on a 200-block one.
            double perimeter = 2 * (width + depth);
            double step = Math.Max(1.0, perimeter / maxParticles);

            var props = MakeProps(color);

            for (double x = x1; x < x2; x += step)
            {
                Spawn(sapi, props, x, y, z1);
                Spawn(sapi, props, x, y, z2);
            }
            for (double z = z1; z < z2; z += step)
            {
                Spawn(sapi, props, x1, y, z);
                Spawn(sapi, props, x2, y, z);
            }

            // The four corners always, whatever the step landed on: they are what tells you the shape.
            Spawn(sapi, props, x1, y, z1);
            Spawn(sapi, props, x2, y, z1);
            Spawn(sapi, props, x1, y, z2);
            Spawn(sapi, props, x2, y, z2);
        }

        private static void Spawn(ICoreServerAPI sapi, SimpleParticleProperties props,
                                  double x, double y, double z)
        {
            props.MinPos = new Vec3d(x, y, z);
            sapi.World.SpawnParticles(props);
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
