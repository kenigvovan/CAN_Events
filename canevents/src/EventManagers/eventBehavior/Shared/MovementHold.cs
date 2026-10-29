using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace canevents.src.EventManagers.eventBehavior.Shared
{
    // Keeps every body of a match where it stands for a while (HG pedestals, battle intermission):
    // a player who wanders more than a block is put back, a bot's AI is stopped. Combat is the caller's
    // business (CombatSuspended, damage hooks).
    public sealed class MovementHold
    {
        private readonly RunningEvent match;
        private readonly string errorLangKey;
        private readonly Dictionary<string, Vec3d> anchors = new Dictionary<string, Vec3d>();
        private long listenerId = -1;

        // errorLangKey takes the seconds left of the match's hold (RunningEvent.BeginHold).
        public MovementHold(RunningEvent match, string errorLangKey)
        {
            this.match = match;
            this.errorLangKey = errorLangKey;
        }

        public bool Active => listenerId >= 0;

        // Anchors are taken lazily in the tick: teleports to the spawn points are async (wait for chunks).
        public void Begin()
        {
            if (Active) return;
            // New tasks are blocked through this flag (ArenaBotManager.Spawn); running ones in Tick.
            match.MovementFrozen = true;
            anchors.Clear();
            // Match tick, so cleanup unregisters it even if End never runs.
            listenerId = match.RegisterMatchTick(Tick, 250);
        }

        public void End()
        {
            match.MovementFrozen = false;
            match.UnregisterMatchTick(listenerId);
            listenerId = -1;
            anchors.Clear();
        }

        private void Tick(float dt)
        {
            // BodyOf, not PlayerByUid: bots are held too.
            foreach (var uid in match.ParticipantsBefore)
            {
                if (!match.IsActive(uid)) continue;
                var entity = match.BodyOf(uid);
                if (entity == null) continue;
                var pos = entity.Pos;

                StopAi(entity);

                // Stacked teleports reset chunk sending (Onplrteleported sets CurrentChunkSentRadius = 0).
                if (entity.Teleporting) continue;

                // Plain Y, not pos.XYZ: XYZ is InternalY (Y + dim*32768), TeleportToDouble wants in-dimension Y.
                if (!anchors.TryGetValue(uid, out var anchor))
                {
                    anchors[uid] = new Vec3d(pos.X, pos.Y, pos.Z);
                    continue;
                }

                double dx = pos.X - anchor.X;
                double dz = pos.Z - anchor.Z;
                double distSq = dx * dx + dz * dz;

                // >4 blocks in 250ms is a teleport landing, not movement: re-anchor.
                if (distSq > 16.0)
                {
                    anchors[uid] = new Vec3d(pos.X, pos.Y, pos.Z);
                    continue;
                }

                if (distSq > 1.0)
                {
                    entity.TeleportToDouble(anchor.X, anchor.Y, anchor.Z);
                    (match.sapi.World.PlayerByUid(uid) as IServerPlayer)?.SendIngameError(
                        "canevents-hold", Lang.Get(errorLangKey,
                            (int)Math.Ceiling(Math.Max(0f, match.HoldEndsAtElapsed - match.ElapsedSeconds))));
                }
            }
        }

        // The flag only blocks new tasks; running ones must be stopped here.
        private static void StopAi(Entity entity)
        {
            if (entity is not EntityAgent agent || entity is EntityPlayer) return;

            var taskAi = agent.GetBehavior<EntityBehaviorTaskAI>();
            if (taskAi != null)
            {
                taskAi.TaskManager.StopTasks();
                taskAi.PathTraverser?.Stop();
            }

            agent.Controls.StopAllMovement();
            agent.Pos.Motion.X = 0;
            agent.Pos.Motion.Z = 0;
        }
    }
}
