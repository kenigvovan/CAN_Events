using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace canevents.src.Mobs.Ai
{
    public static class RaiderSenses
    {
        // A match mob asks its director and never scans; a roster bot picks its own opponent.
        public static Entity TargetOf(Entity raider)
            => MatchMobLink.IsMatchMob(raider)
                ? MatchMobManager.Of(MatchMobLink.MatchIdOf(raider))?.TargetOf(raider)
                : Bots.BotTargeting.TargetOf(raider);

        // World angle (sin/cos convention, like Face) to come in from, NaN when the director has no place for it.
        public static double ApproachAngleOf(Entity raider, Entity target)
        {
            float offset = MatchMobManager.Of(MatchMobLink.MatchIdOf(raider))?.ApproachOf(raider) ?? float.NaN;
            if (float.IsNaN(offset)) return double.NaN;
            var view = target.Pos.GetViewVector();
            return Math.Atan2(-view.X, -view.Z) + offset;
        }

        public static double Distance(Entity a, Entity b)
        {
            double dx = a.Pos.X - b.Pos.X, dy = a.Pos.Y - b.Pos.Y, dz = a.Pos.Z - b.Pos.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        // Solid blocks only: grass and flowers don't stop an arrow.
        public static bool CanSee(Entity from, Entity to)
        {
            var eye = from.Pos.XYZ.Add(0, from.LocalEyePos.Y, 0);
            var aim = to.Pos.XYZ.Add(0, to.LocalEyePos.Y * 0.8, 0);
            BlockSelection block = null;
            EntitySelection hit = null;
            from.World.RayTraceForSelection(eye, aim, ref block, ref hit,
                (pos, b) => b.CollisionBoxes != null && b.CollisionBoxes.Length > 0, e => false);
            return block == null;
        }

        // Same side: fellow match mobs for a mob; for a bot, participants it may not attack.
        public static bool IsAlly(Entity self, Entity other)
        {
            if (other == null || other == self || !other.Alive) return false;
            if (MatchMobLink.IsMatchMob(self)) return MatchMobLink.IsMatchMob(other);
            return Bots.BotMatchLink.UidOf(other) != null && !Bots.BotMatchLink.IsOpponent(self, other);
        }

        // An ally standing in the shot: better hold the arrow than hit a friend's back.
        public static bool LaneBlockedByAlly(Entity from, Entity to)
        {
            var eye = from.Pos.XYZ.Add(0, from.LocalEyePos.Y, 0);
            var aim = to.Pos.XYZ.Add(0, to.LocalEyePos.Y * 0.7, 0);
            BlockSelection block = null;
            EntitySelection hit = null;
            from.World.RayTraceForSelection(eye, aim, ref block, ref hit, (pos, b) => false, e => IsAlly(from, e));
            return hit != null;
        }

        public static Entity NearestAlly(Entity self, float range)
            => self.World.GetNearestEntity(self.Pos.XYZ, range, 2f, e => IsAlly(self, e));

        // Somewhere to stand: solid floor, no water, room for the body.
        public static bool Footing(Entity self, Vec3d at)
        {
            var blocks = self.World.BlockAccessor;
            // at.Y is the internal Y (dimension included), so the BlockPos stays in dimension 0 terms.
            var pos = new BlockPos((int)Math.Floor(at.X), (int)Math.Floor(at.Y), (int)Math.Floor(at.Z), 0);
            // One block down is still a step, two is a drop.
            bool floor = Solid(blocks, pos.DownCopy()) || Solid(blocks, pos.DownCopy(2));
            if (!floor) return false;
            if (blocks.GetBlock(pos, BlockLayersAccess.Fluid)?.IsLiquid() == true) return false;
            return !self.World.CollisionTester.IsColliding(blocks, self.SelectionBox, new Vec3d(at.X, pos.Y + 0.01, at.Z), false);
        }

        private static bool Solid(IBlockAccessor blocks, BlockPos pos)
            => blocks.GetBlock(pos)?.SideSolid[BlockFacing.UP.Index] == true;

        public static void Face(Entity self, Entity target)
            => self.Pos.Yaw = (float)Math.Atan2(target.Pos.X - self.Pos.X, target.Pos.Z - self.Pos.Z);
    }
}
