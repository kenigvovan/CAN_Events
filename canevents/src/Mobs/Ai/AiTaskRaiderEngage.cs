using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace canevents.src.Mobs.Ai
{
    // Movement for a raider: close in on the director's target, or for an archer hold the profile's
    // distance band and sidestep while in it. After a hit-and-run strike it backs off for a moment.
    // Paths are only recomputed when the target has moved, short moves walk straight.
    public class AiTaskRaiderEngage : AiTaskBase
    {
        private const int PlanIntervalMs = 600;
        private const double RepathWhenMovedSq = 2.5 * 2.5;
        private const double FlankUntil = 4.5;
        private const double InFaceRange = 2.6;

        private enum Mode { Approach, Hold, Retreat, Sidestep }

        private readonly float moveSpeed;
        private RaiderProfile profile;
        private Entity target;
        private Mode mode;
        private long nextPlanMs;
        private bool stuck;
        private int side = 1;
        private float pace = 1f;
        private readonly Vec3d goal = new Vec3d();
        private readonly Vec3d pathGoal = new Vec3d();
        private bool hasPath;

        public AiTaskRaiderEngage(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
            : base(entity, taskConfig, aiConfig)
        {
            moveSpeed = taskConfig["movespeed"].AsFloat(0.03f);
        }

        public override bool ShouldExecute()
        {
            target = RaiderSenses.TargetOf(entity);
            return target != null;
        }

        public override void StartExecute()
        {
            base.StartExecute();
            profile = RaiderProfiles.Of(entity);
            // A little of its own pace, so a pack doesn't march in step.
            pace = 0.9f + (float)world.Rand.NextDouble() * 0.2f;
            stuck = false;
            hasPath = false;
            nextPlanMs = 0;
        }

        public override bool ContinueExecute(float dt)
        {
            target = RaiderSenses.TargetOf(entity);
            if (target == null) return false;

            long now = world.ElapsedMilliseconds;
            if (stuck || now >= nextPlanMs) Plan(now);
            if (mode == Mode.Hold) RaiderSenses.Face(entity, target);
            return true;
        }

        private void Plan(long now)
        {
            nextPlanMs = now + PlanIntervalMs;
            double dist = RaiderSenses.Distance(entity, target);
            float speed = moveSpeed * profile.SpeedMul * pace;
            bool backingOff = entity.Attributes.GetLong(RaiderProfiles.RetreatUntilAttribute) > now;

            if (stuck)
            {
                stuck = false;
                side = -side;
                Walk(Mode.Sidestep, Sideways(3.0), speed);
            }
            else if (backingOff || (profile.Ranged && dist < profile.MinRange))
                Walk(Mode.Retreat, Footed(Away(profile.Ranged ? profile.MinRange + 3f - dist : 4.0)), speed * 1.1f);
            else if (profile.Ranged && dist <= profile.MaxRange && RaiderSenses.CanSee(entity, target))
            {
                if (world.Rand.NextDouble() < 0.3) side = -side;
                Walk(Mode.Hold, Footed(Sideways(2.5)), speed * 0.6f);
            }
            else if (!profile.Ranged && dist <= InFaceRange)
            {
                // In its face: give a crowding ally room, otherwise circle between swings.
                var ally = RaiderSenses.NearestAlly(entity, 1.3f);
                if (ally != null) Walk(Mode.Hold, Footed(AwayFrom(ally, 1.5)), speed * 0.7f);
                else
                {
                    if (world.Rand.NextDouble() < 0.25) side = -side;
                    Walk(Mode.Hold, Footed(Sideways(1.5)), speed * 0.5f);
                }
            }
            else Approach(speed, profile.Ranged ? profile.MaxRange * 0.6f : 1.2f, dist);
        }

        // From afar: to its place on a ring around the target (its back first), so the pack closes in
        // from several sides. Up close, or for a boss, straight in, leading a running target a little.
        private void Approach(float speed, float stopAt, double dist)
        {
            var motion = target.Pos.Motion;
            double angle = profile.Id == "boss" ? double.NaN : RaiderSenses.ApproachAngleOf(entity, target);
            if (!double.IsNaN(angle) && dist > FlankUntil)
            {
                double ring = GameMath.Clamp(dist * 0.5, 3.5, 8.0);
                goal.Set(target.Pos.X + Math.Sin(angle) * ring, target.Pos.InternalY, target.Pos.Z + Math.Cos(angle) * ring);
                stopAt = 1f;
            }
            else goal.Set(target.Pos.X + motion.X * 8, target.Pos.InternalY, target.Pos.Z + motion.Z * 8);
            if (mode == Mode.Approach && hasPath && pathTraverser.Active && goal.SquareDistanceTo(pathGoal) < RepathWhenMovedSq) return;

            mode = Mode.Approach;
            pathGoal.Set(goal);
            hasPath = pathTraverser.NavigateTo(goal.Clone(), speed, stopAt, OnGoalReached, OnStuck, null, false, 2000);
            if (!hasPath) Walk(Mode.Approach, goal, speed);
        }

        private void Walk(Mode next, Vec3d to, float speed)
        {
            mode = next;
            hasPath = false;
            pathTraverser.WalkTowards(to.Clone(), speed, 0.5f, OnGoalReached, OnStuck);
        }

        private Vec3d Away(double distance)
        {
            double dx = entity.Pos.X - target.Pos.X, dz = entity.Pos.Z - target.Pos.Z;
            double len = Math.Max(0.01, Math.Sqrt(dx * dx + dz * dz));
            distance = Math.Max(2.0, distance);
            return goal.Set(entity.Pos.X + dx / len * distance, entity.Pos.InternalY, entity.Pos.Z + dz / len * distance);
        }

        // A step onto a ledge, into water or a wall is no step: try the other side, else stay put.
        private Vec3d Footed(Vec3d step)
        {
            if (RaiderSenses.Footing(entity, step)) return step;
            side = -side;
            var other = Sideways(1.5);
            return RaiderSenses.Footing(entity, other) ? other : goal.Set(entity.Pos.X, entity.Pos.InternalY, entity.Pos.Z);
        }

        private Vec3d AwayFrom(Entity other, double distance)
        {
            double dx = entity.Pos.X - other.Pos.X, dz = entity.Pos.Z - other.Pos.Z;
            double len = Math.Max(0.01, Math.Sqrt(dx * dx + dz * dz));
            return goal.Set(entity.Pos.X + dx / len * distance, entity.Pos.InternalY, entity.Pos.Z + dz / len * distance);
        }

        private Vec3d Sideways(double distance)
        {
            double dx = target.Pos.X - entity.Pos.X, dz = target.Pos.Z - entity.Pos.Z;
            double len = Math.Max(0.01, Math.Sqrt(dx * dx + dz * dz));
            return goal.Set(entity.Pos.X - dz / len * distance * side, entity.Pos.InternalY, entity.Pos.Z + dx / len * distance * side);
        }

        private void OnGoalReached() => hasPath = false;

        private void OnStuck() => stuck = true;

        public override void FinishExecute(bool cancelled)
        {
            base.FinishExecute(cancelled);
            pathTraverser.Stop();
            hasPath = false;
        }
    }
}
