using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace canevents.src.Compat
{
    /// <summary>
    /// Interop with the "Downed" mod, which turns a killing blow into a knocked-down state instead of
    /// a death. Nothing here references its assembly: every piece of state it keeps lives in
    /// <see cref="Entity.WatchedAttributes"/> or in the entity's walkspeed stats, so each check is a
    /// string lookup and "the mod is not installed" reads as "the attribute was never set".
    ///
    /// Two things go wrong without this shim:
    /// <list type="bullet">
    /// <item>Downed intercepts the lethal hit AFTER our own onDamaged hook ran (it re-subscribes
    /// itself last on every damage event), so a match cleared the player's inventory for a death that
    /// never happened.</item>
    /// <item>While a player is down, Downed squeezes their collision box and eye height and relies on
    /// <c>EntityPlayer.updateEyeHeight</c> growing them back. That method returns early in Spectator
    /// mode, so anyone we move to the spectator seat stays squashed forever - see
    /// <see cref="HarmonyPatches.SpectatorBodyMetricsPatch"/>.</item>
    /// </list>
    /// </summary>
    public static class DownedCompat
    {
        /// <summary>PropertyName() of Downed's entity behavior. Its presence on a body is what tells
        /// us the mod is live, with no hard reference to it.</summary>
        public const string BehaviorName = "playerdowned";

        // Every WatchedAttributes flag Downed sets on a player. Cleared as a group: any one of them
        // left set keeps part of its client-side lock (camera, animation, head yaw) alive.
        private static readonly string[] StateFlags =
        {
            "downed", "dragged", "dragging", "stowed", "executing", "beingexecuted", "downeddied"
        };

        // Walkspeed stat codes Downed sets. "downed" is the crawl slowdown; the other two are its
        // -10 immobilizers (execution, bandaging), which read as "cannot move at all".
        private static readonly string[] WalkSpeedCodes = { "downed", "executing", "downedhealing" };

        /// <summary>Is Downed handling this body? Asked of the entity itself, so it is also false for
        /// one the mod chose not to attach to.</summary>
        public static bool Handles(Entity entity) => entity?.GetBehavior(BehaviorName) != null;

        /// <summary>Is this entity knocked down right now?</summary>
        public static bool IsDowned(Entity entity)
            => entity?.WatchedAttributes?.GetBool("downed", false) ?? false;

        /// <summary>
        /// Will Downed swallow this killing blow and knock the entity down instead of letting it die?
        /// Asked from our lethal-damage hooks, which run before Downed's, so "this hit is lethal" does
        /// not mean "this player is about to die".
        ///
        /// Deliberately optimistic: when Downed's own cooldown makes it decline the interception the
        /// player really does die, and the death-time handler still clears the inventory before the
        /// engine's drop task runs (<c>Entity.Die</c> triggers the death event first, and the player
        /// inventory drop is queued onto the next tick). So guessing "downed" costs nothing, while
        /// guessing "dead" wipes a living player's belongings.
        /// </summary>
        public static bool WouldGoDownInsteadOfDying(Entity entity, DamageSource source)
        {
            if (!Handles(entity)) return false;
            if (IsDowned(entity)) return false;      // already down: the next lethal hit is a real death
            if (source == null) return false;        // /kill and void falls bypass Downed entirely
            if (source.Type == EnumDamageType.Heal) return false;
            return true;
        }

        /// <summary>
        /// Drops every trace of the knocked-down state from a player: flags, movement locks, the seat
        /// they were dragged or stowed on, and the squeezed body metrics. Called whenever the match
        /// takes a player out of normal play - into the spectator seat, or home at the end - because
        /// from that point on nothing inside Downed will run to undo any of it.
        /// </summary>
        public static void Clear(IServerPlayer player) => Clear(player?.Entity);

        /// <summary>Entity-level overload; <see cref="Clear(IServerPlayer)"/> is the usual entry.</summary>
        public static void Clear(Entity entity)
        {
            if (entity == null) return;

            // Seats first: Downed's drag and stow seats are mounts, and a mounted player has
            // FreeMove forced off by the engine (EntityPlayer.TryMount).
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

            // Downed zeroes regeneration while a player is down and restores it on the way out; that
            // way out never runs for a player we pulled aside.
            entity.WatchedAttributes.SetFloat("regenSpeed", 1f);

            RestoreBodyMetrics(entity as EntityPlayer);
        }

        /// <summary>
        /// Puts the collision box, selection box and eye height back to what the entity type declares.
        /// Cheap enough to call every tick: it compares before it writes, so a body already at its
        /// normal size allocates nothing.
        /// </summary>
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
                // Downed also pushes the eye forward while crawling, hence X/Z back to zero as well.
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
