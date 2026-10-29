using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace canevents.src.Compat
{
    // Downed mod interop via WatchedAttributes/stats only, no assembly reference.
    // Downed intercepts lethal hits after our onDamaged hook, and its squashed body metrics never
    // recover in Spectator mode (see SpectatorBodyMetricsPatch).
    public static class DownedCompat
    {
        public const string BehaviorName = "playerdowned";

        // Clear all together: any leftover keeps part of the client lock (camera, anim, head yaw).
        private static readonly string[] StateFlags =
        {
            "downed", "dragged", "dragging", "stowed", "executing", "beingexecuted", "downeddied"
        };

        // "downed" = crawl slowdown; the other two are -10 immobilizers (execution, bandaging).
        private static readonly string[] WalkSpeedCodes = { "downed", "executing", "downedhealing" };

        public static bool Handles(Entity entity) => entity?.GetBehavior(BehaviorName) != null;

        public static bool IsDowned(Entity entity)
            => entity?.WatchedAttributes?.GetBool("downed", false) ?? false;

        // Optimistic: a wrong "downed" is still handled on death (vanilla drop is queued to next tick).
        public static bool WouldGoDownInsteadOfDying(Entity entity, DamageSource source)
        {
            if (!Handles(entity)) return false;
            if (IsDowned(entity)) return false;      // already down: the next lethal hit is a real death
            if (source == null) return false;        // /kill and void falls bypass Downed entirely
            if (source.Type == EnumDamageType.Heal) return false;
            return true;
        }

        // Wipes downed state when we pull a player out of play; Downed won't undo it itself.
        public static void Clear(IServerPlayer player) => Clear(player?.Entity);

        public static void Clear(Entity entity)
        {
            if (entity == null) return;

            // Drag/stow seats are mounts; mounted players get FreeMove forced off (EntityPlayer.TryMount).
            if (entity is EntityAgent agent && agent.MountedOn != null) agent.TryUnmount();

            foreach (var flag in StateFlags)
            {
                if (!entity.WatchedAttributes.HasAttribute(flag)) continue;
                entity.WatchedAttributes.SetBool(flag, false);
                entity.WatchedAttributes.MarkPathDirty(flag);
            }
            entity.WatchedAttributes.RemoveAttribute("downedStartMs");
            entity.WatchedAttributes.RemoveAttribute("downedSecondsLeft");

            foreach (var code in WalkSpeedCodes) entity.Stats.Remove("walkspeed", code);

            // Downed zeroes regen while down.
            entity.WatchedAttributes.SetFloat("regenSpeed", 1f);

            RestoreBodyMetrics(entity as EntityPlayer);
        }

        // Cheap enough to call every tick, only writes on difference.
        public static void RestoreBodyMetrics(EntityPlayer entity)
        {
            var props = entity?.Properties;
            if (props == null) return;

            var collision = props.CollisionBoxSize;
            if (collision != null && Differs(entity.CollisionBox, collision.X, collision.Y))
                entity.SetCollisionBox(collision.X, collision.Y);

            var selection = props.SelectionBoxSize ?? collision;
            if (selection != null && Differs(entity.SelectionBox, selection.X, selection.Y))
                entity.SetSelectionBox(selection.X, selection.Y);

            var eye = entity.LocalEyePos;
            if (eye != null && (Math.Abs(eye.Y - props.EyeHeight) > 0.001
                                || Math.Abs(eye.X) > 0.001 || Math.Abs(eye.Z) > 0.001))
            {
                // Downed also shifts the eye forward while crawling.
                eye.Set(0.0, props.EyeHeight, 0.0);
            }
        }

        private static bool Differs(Cuboidf box, float length, float height)
        {
            if (box == null) return true;
            return Math.Abs(box.Y2 - height) > 0.001f
                || Math.Abs(box.X2 - box.X1 - length) > 0.001f;
        }
    }
}
