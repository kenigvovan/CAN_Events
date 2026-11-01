using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace canevents.src.Mobs.Ai
{
    // Archer shot at the director's target: draw, then a real arrow on a ballistic arc with lead.
    // Arrows can't be picked up, so a wave can't be farmed for them.
    public class AiTaskRaiderShoot : AiTaskBase
    {
        private readonly int drawMs;
        private readonly AssetLocation projectileCode;
        private readonly float projectileDamage;
        private readonly double projectileSpeed;
        private readonly float spreadDeg;

        private Entity target;
        private long startedMs;
        private bool fired;

        public AiTaskRaiderShoot(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
            : base(entity, taskConfig, aiConfig)
        {
            drawMs = taskConfig["drawMs"].AsInt(1100);
            projectileCode = new AssetLocation(taskConfig["projectile"].AsString("game:arrow-copper"));
            projectileDamage = taskConfig["projectileDamage"].AsFloat(3f);
            projectileSpeed = taskConfig["projectileSpeed"].AsFloat(32f);
            spreadDeg = taskConfig["spreadDeg"].AsFloat(3f);
            MinCooldownMs = taskConfig["mincooldown"].AsInt(1200);
            MaxCooldownMs = Math.Max(MinCooldownMs + 1, taskConfig["maxcooldown"].AsInt(2200));
        }

        public override bool ShouldExecute()
        {
            if (IsOnCooldown()) return false;
            var profile = RaiderProfiles.Of(entity);
            if (!profile.Ranged) return false;

            target = RaiderSenses.TargetOf(entity);
            if (target == null) return false;
            double dist = RaiderSenses.Distance(entity, target);
            return dist >= 3.0 && dist <= profile.MaxRange + 6f && RaiderSenses.CanSee(entity, target)
                   && !RaiderSenses.LaneBlockedByAlly(entity, target);
        }

        public override void StartExecute()
        {
            base.StartExecute();
            pathTraverser.Stop();
            startedMs = world.ElapsedMilliseconds;
            fired = false;
        }

        public override bool ContinueExecute(float dt)
        {
            target = RaiderSenses.TargetOf(entity);
            if (target == null) return false;
            RaiderSenses.Face(entity, target);

            long held = world.ElapsedMilliseconds - startedMs;
            if (!fired && held >= drawMs)
            {
                fired = true;
                if (RaiderSenses.CanSee(entity, target) && !RaiderSenses.LaneBlockedByAlly(entity, target)) Fire();
            }
            return held < drawMs + 300;
        }

        private void Fire()
        {
            var type = world.GetEntityType(projectileCode);
            var item = world.GetItem(projectileCode);
            if (type == null || item == null) return;
            if (!(world.ClassRegistry.CreateEntity(type) is IProjectile projectile)) return;
            var arrow = (Entity)projectile;

            projectile.FiredBy = entity;
            projectile.Damage = projectileDamage;
            projectile.DamageTier = 1;
            projectile.DamageType = EnumDamageType.PiercingAttack;
            projectile.ProjectileStack = new ItemStack(item, 1);
            projectile.Collectible = false;
            projectile.DropOnImpactChance = 0f;

            var from = entity.Pos.XYZ.Add(0, entity.LocalEyePos.Y, 0);
            var aim = target.Pos.XYZ.Add(0, target.LocalEyePos.Y * 0.7, 0);
            var velocity = Aim(from, aim);

            arrow.Pos.SetPosWithDimension(entity.Pos.AheadCopy(0.4).XYZ.Add(0, entity.LocalEyePos.Y, 0));
            arrow.Pos.Motion.Set(velocity);
            arrow.World = world;
            projectile.PreInitialize();
            world.SpawnPriorityEntity(arrow);
            world.PlaySoundAt(new AssetLocation("game:sounds/bow-release"), entity, null, true, 24f);
        }

        // Motion is in blocks per tick-minute like vanilla's; the low arc of the two solutions, with lead and spread.
        private Vec3d Aim(Vec3d from, Vec3d to)
        {
            const double perMinute = 1.0 / 60.0;
            double speed = projectileSpeed * perMinute;
            double gravity = GlobalConstants.GravityPerSecond * perMinute;

            double flight = from.DistanceTo(to) / speed;
            var lead = to.Clone().Add(target.Pos.Motion.X * flight, 0, target.Pos.Motion.Z * flight);

            double dx = lead.X - from.X, dy = lead.Y - from.Y, dz = lead.Z - from.Z;
            double horizontal = Math.Sqrt(dx * dx + dz * dz);
            double speedSq = speed * speed;
            double disc = speedSq * speedSq - gravity * (gravity * horizontal * horizontal + 2 * dy * speedSq);

            double pitch = disc >= 0
                ? Math.Atan2(speedSq - Math.Sqrt(disc), gravity * horizontal)
                : Math.PI / 4;
            double yaw = Math.Atan2(dx, dz);

            double spread = spreadDeg * GameMath.DEG2RAD;
            pitch += (world.Rand.NextDouble() - 0.5) * spread;
            yaw += (world.Rand.NextDouble() - 0.5) * spread;

            double flat = speed * Math.Cos(pitch);
            return new Vec3d(Math.Sin(yaw) * flat, speed * Math.Sin(pitch), Math.Cos(yaw) * flat);
        }
    }
}
