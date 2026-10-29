using System;
using canevents.src.EventManagers.eventBehavior;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace canevents.src
{
    // Keeps arena coords out of vanilla's corpse return teleporter (ModSystemCorpseReturnTeleporter.lastDeathLocations).
    // MarkMatchDeath stores a pending fix in ModData on death; ApplyPending applies it on respawn/login/match end,
    // so we never race vanilla's PlayerDeath handler. A non-match death clears the pending fix.
    public static class CorpseReturnGuard
    {
        // Lives in ModData so it survives a restart.
        private const string PendingModDataKey = "canevents_corpse_pending";

        // false = drop the vanilla record instead of moving it.
        private const string HasTargetKey = "hasTarget";

        // Target is TPThereAndBack's return marker, read now while it still exists.
        public static void MarkMatchDeath(IServerPlayer player)
        {
            if (player == null) return;

            // No marker -> task without target, which drops the arena record.
            EventManagers.ReturnPosMarker.TryRead(player, null, out var target);
            var task = new TreeAttribute();
            task.SetBool(HasTargetKey, target != null);
            if (target != null)
            {
                task.SetInt("x", target.X);
                task.SetInt("y", target.Y);
                task.SetInt("z", target.Z);
                // Older builds have no "dim", reads as 0.
                task.SetInt("dim", target.dimension);
            }
            player.SetModdata(PendingModDataKey, task.ToBytes());
        }

        // Non-match death: the latest death should win.
        public static void ForgetPending(IServerPlayer player)
            => player?.RemoveModdata(PendingModDataKey);

        // With no target the record is removed; vanilla GetTarget then returns null and doesn't consume the gear.
        public static void ApplyPending(ICoreServerAPI sapi, IServerPlayer player)
        {
            if (sapi == null || player == null) return;

            byte[] data = player.GetModdata(PendingModDataKey);
            if (data == null || data.Length == 0) return;

            var log = Log.For(sapi.World?.Logger, "corpse-return");
            try
            {
                var task = new TreeAttribute();
                task.FromBytes(data);
                bool hasTarget = task.GetBool(HasTargetKey);
                // Same as vanilla's Event_PlayerDeath (Entity.Pos.XYZ): Y includes dim*DimensionBoundary.
                var target = hasTarget
                    ? new Vec3d(task.GetInt("x") + 0.5,
                                task.GetInt("y") + task.GetInt("dim") * BlockPos.DimensionBoundary + 0.5,
                                task.GetInt("z") + 0.5)
                    : null;

                // Null if the corpse return mod system is disabled; task is still cleared in finally.
                var locations = sapi.ModLoader?.GetModSystem<ModSystemCorpseReturnTeleporter>()?.lastDeathLocations;
                if (locations != null)
                {
                    if (target != null)
                    {
                        locations[player.PlayerUID] = target;
                        log.Notification("{0}: corpse return point moved off the arena to the spot the "
                            + "match picked them up from ({1})", player.PlayerName, target);
                    }
                    else if (locations.Remove(player.PlayerUID))
                    {
                        log.Notification("{0}: corpse return point dropped - they died in a match with "
                            + "no return marker to point at", player.PlayerName);
                    }
                }
            }
            catch (Exception e)
            {
                log.Error("applying the pending corpse return fix for {0}: {1}", player.PlayerName, e);
            }
            finally
            {
                player.RemoveModdata(PendingModDataKey);
            }
        }
    }
}
