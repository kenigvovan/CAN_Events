using System;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace canevents.src.EventManagers.eventBehavior.Shared
{
    // Particle zone borders. Server-side SpawnParticles only reaches clients in range.
    public static class ZoneVisualizer
    {
        private static readonly int NeutralColor = ColorUtil.ToRgba(255, 180, 180, 180);
        private static readonly int ContestedColor = ColorUtil.ToRgba(255, 60, 60, 255);

        // Light blue (the storm deals Frost damage), semi-transparent.
        public static readonly int StormColor = ColorUtil.ToRgba(170, 255, 210, 160);

        public static readonly int PhaseRingColor = ColorUtil.ToRgba(200, 200, 200, 200);

        // Same green the admin panel uses for spawns.
        public static readonly int SpawnMarkerColor = ColorUtil.ToRgba(255, 80, 230, 80);
        public static readonly int SponsorMarkerColor = ColorUtil.ToRgba(255, 255, 90, 60);

        // ownerTeam: >=0 owning team, KingOfTheHillEB.OwnerNeutral, or OwnerContested.
        public static int OwnerColor(int ownerTeam)
        {
            if (ownerTeam == KingOfTheHillEB.OwnerContested) return ContestedColor;
            if (ownerTeam < 0) return NeutralColor;
            return TeamPalette.Packed(ownerTeam);
        }

        // Team palette minus green, so a loot spot never reads as a spawn marker.
        private static readonly int[] LootTierTeams = { 0, 1, 3, 4, 5 };

        // tier < 0 (unknown tier) is the neutral grey.
        public static int LootTierColor(int tier)
            => tier < 0 ? NeutralColor : TeamPalette.Packed(LootTierTeams[tier % LootTierTeams.Length]);

        // baseY is the floor surface (blockY + 1), otherwise the bottom ring is hidden in the block.
        // groundY (surface at x,z, NaN if unknown) drops the bottom ring onto lower terrain, so the wall doesn't float.
        public static void DrawDome(ICoreServerAPI sapi, double cx, double baseY, double cz,
                                    float radius, int color, System.Func<double, double, double> groundY = null)
        {
            if (sapi == null || radius <= 0f) return;

            var props = MakeStormProps(color);

            const int bands = 5; // 0°..72°; the 90° cap is the single particle below
            for (int b = 0; b < bands; b++)
            {
                double lat = b * (Math.PI / 2.0) / bands;
                float ringR = radius * (float)Math.Cos(lat);
                double y = baseY + radius * Math.Sin(lat);
                // Capped: one packet per particle; too many crash the client's ParticlePoolQuads.
                int segments = Math.Min(24, Math.Max(6, (int)(ringR * 0.6f)));
                if (ringR <= 0f) continue;
                for (int i = 0; i < segments; i++)
                {
                    double angle = i * 2.0 * Math.PI / segments;
                    double x = cx + Math.Cos(angle) * ringR, z = cz + Math.Sin(angle) * ringR;
                    props.MinPos = new Vec3d(x, y, z);
                    sapi.World.SpawnParticles(props);

                    if (b != 0 || groundY == null) continue;
                    double ground = groundY(x, z);
                    if (double.IsNaN(ground) || ground >= y - 1) continue;
                    // A few particles down to the ground, not a full column: same particle budget.
                    double step = Math.Max(2.0, (y - ground) / 3);
                    for (double wy = y - step; wy > ground - 0.5; wy -= step)
                    {
                        props.MinPos = new Vec3d(x, Math.Max(ground, wy), z);
                        sapi.World.SpawnParticles(props);
                    }
                }
            }

            props.MinPos = new Vec3d(cx, baseY + radius, cz);
            sapi.World.SpawnParticles(props);
        }

        // Fresh instance per call: DrawDome mutates MinPos.
        private static SimpleParticleProperties MakeStormProps(int color)
        {
            var props = MakeProps(color);
            props.MinSize = 0.5f;
            props.MaxSize = 0.9f;
            props.MinVelocity = new Vec3f(-0.05f, 0.05f, -0.05f);
            props.AddVelocity = new Vec3f(0.1f, 0.25f, 0.1f);
            props.LifeLength = 1.6f;
            props.VertexFlags = 120; // mild glow, visible at night
            return props;
        }

        // Mod colours are packed like block highlights (ColorFromRgba); particles want ToRgba order, so swap R/B.
        private static SimpleParticleProperties MakeProps(int color)
        {
            return new SimpleParticleProperties(
                1, 1,
                ColorUtil.ReverseColorBytes(color),
                new Vec3d(), new Vec3d(),
                new Vec3f(), new Vec3f(),
                // > 1s redraw interval, so rings don't blink.
                1.2f,
                0f,
                0.3f, 0.3f,
                EnumParticleModel.Quad);
        }

        public static readonly int ArenaBorderColor = ColorUtil.ToRgba(120, 190, 215, 240);

        // Step comes from the perimeter so big arenas stay within the particle budget (see DrawDome).
        public static void DrawBox(ICoreServerAPI sapi, BlockPos min, BlockPos max, int color,
                                   int maxParticles = 48)
        {
            if (sapi == null || min == null || max == null || maxParticles < 4) return;

            double x1 = Math.Min(min.X, max.X), x2 = Math.Max(min.X, max.X) + 1;
            double z1 = Math.Min(min.Z, max.Z), z2 = Math.Max(min.Z, max.Z) + 1;
            double y = Math.Max(min.Y, max.Y) + 1.2;

            double width = x2 - x1, depth = z2 - z1;
            if (width <= 0 || depth <= 0) return;

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

            // Corners always.
            Spawn(sapi, props, x1, y, z1);
            Spawn(sapi, props, x2, y, z1);
            Spawn(sapi, props, x1, y, z2);
            Spawn(sapi, props, x2, y, z2);
        }

        public static void DrawPoint(ICoreServerAPI sapi, double x, double y, double z, int color)
        {
            if (sapi == null) return;
            Spawn(sapi, MakeProps(color), x, y, z);
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
