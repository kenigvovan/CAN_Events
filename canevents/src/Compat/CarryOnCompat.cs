using System;
using System.Collections.Generic;
using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace canevents.src.Compat
{
    /// <summary>
    /// Interop with the "Carry On" mod, which lets a player carry a chest (or any carryable block) in
    /// their hands or on their back. What is carried does NOT live in any inventory: it sits in
    /// <c>Entity.WatchedAttributes["carryon:Carried"]</c> as one subtree per carry slot, holding the
    /// block's ItemStack plus the block entity's own data. So a match that empties every inventory
    /// still teleports the player to the arena with the chest on their back - and CarryOn's death
    /// handler drops it, contents and all, the first time they die there.
    ///
    /// This shim takes the carried blocks off for the duration of the match and puts them back with
    /// the rest of the player's belongings. The snapshot is a plain copy of the attribute subtree, so
    /// it rides along inside <see cref="EventManagers.PlayerInventoryBackup"/>'s ModData blob and
    /// survives a crash or a reconnect exactly like the inventory does.
    ///
    /// Nothing here references CarryOn's assembly. The preferred path calls its CarryManager through
    /// reflection - only that knows how to undo the walkspeed and hunger stats, the carry animation
    /// and the hotbar locks a carried block installs - and when the mod is absent (or its API moved)
    /// every entry point degrades to writing the attributes directly.
    /// </summary>
    public static class CarryOnCompat
    {
        /// <summary>Root of CarryOn's per-entity carry state. Same key in CarryOn 1.x and 2.x.</summary>
        public const string CarriedAttribute = "carryon:Carried";

        /// <summary>Sibling of the slot subtrees, not a slot: the revision the client renders off.</summary>
        private const string RevisionKey = "Rev";

        /// <summary>Set inside a slot subtree while a carry animation plays.</summary>
        private const string AnimationKey = "Animation";

        /// <summary>The carried block's stack, inside a slot subtree.</summary>
        private const string StackKey = "Stack";

        /// <summary>Stat categories a carried block writes to, under the code "carryon:&lt;Slot&gt;".</summary>
        private static readonly string[] StatCategories = { "walkspeed", "hungerrate" };

        /// <summary>True while this entity carries anything at all.</summary>
        public static bool IsCarrying(Entity entity) => SlotNames(entity).Count > 0;

        /// <summary>
        /// A detached copy of the entity's carry state, or null when it carries nothing. Handed back to
        /// <see cref="Restore"/> later; safe to serialize into ModData in between.
        /// </summary>
        public static ITreeAttribute Snapshot(Entity entity)
        {
            var carried = entity?.WatchedAttributes?.GetTreeAttribute(CarriedAttribute);
            if (carried == null || SlotNames(entity).Count == 0) return null;
            return carried.Clone() as ITreeAttribute;
        }

        /// <summary>
        /// Takes every carried block off the entity without dropping it, and returns how many slots
        /// were emptied. This DISCARDS them - call <see cref="Snapshot"/> first if they are meant to
        /// come back.
        /// </summary>
        public static int Clear(IServerPlayer player) => Clear(player?.Entity);

        /// <summary>Entity-level overload; <see cref="Clear(IServerPlayer)"/> is the usual entry.</summary>
        public static int Clear(Entity entity)
        {
            var slots = SlotNames(entity);
            if (slots.Count == 0) return 0;

            var carryApi = Resolve(entity.Api);
            foreach (var slotName in slots)
            {
                if (carryApi != null && carryApi.Remove(entity, slotName)) continue;
                ClearSlotByHand(entity, slotName);
            }

            // A by-hand clear leaves the subtree in place; drop the whole root and bump the revision so
            // the client stops rendering what is no longer there.
            if (SlotNames(entity).Count > 0) WriteRoot(entity, null);

            UnlockHotbar(entity as EntityPlayer);
            return slots.Count;
        }

        /// <summary>
        /// Puts a <see cref="Snapshot"/> back on the entity. A null snapshot does nothing, so callers
        /// can hand over whatever they stored without checking first.
        /// </summary>
        public static void Restore(IServerPlayer player, ITreeAttribute snapshot) => Restore(player?.Entity, snapshot);

        /// <summary>Entity-level overload; <see cref="Restore(IServerPlayer, ITreeAttribute)"/> is the usual entry.</summary>
        public static void Restore(Entity entity, ITreeAttribute snapshot)
        {
            if (entity == null || snapshot == null) return;

            // Anything picked up during the match comes off first: the snapshot is the authority on
            // what this entity carries, and two blocks cannot share a slot.
            Clear(entity);

            if (!(snapshot.Clone() is ITreeAttribute restored)) return;
            WriteRoot(entity, restored);

            var slots = SlotNames(entity);
            if (slots.Count == 0) return;

            var carryApi = Resolve(entity.Api);
            foreach (var slotName in slots)
            {
                // Read back through CarryOn itself: it deserializes the subtree we just wrote into its
                // own CarriedBlock (resolving the stack and the block entity data), and setting that
                // same block reapplies the stats, the animation and the hotbar locks. Nothing here has
                // to know what a CarriedBlock looks like.
                if (carryApi != null && carryApi.Reapply(entity, slotName)) continue;
                RestoreSlotByHand(entity, slotName);
            }
        }

        /// <summary>Names of the carry slots currently in use - the subtrees under the root, minus the
        /// revision counter that sits alongside them.</summary>
        private static List<string> SlotNames(Entity entity)
        {
            var names = new List<string>();
            var carried = entity?.WatchedAttributes?.GetTreeAttribute(CarriedAttribute);
            if (carried == null) return names;

            foreach (var pair in carried)
            {
                if (pair.Key == RevisionKey) continue;
                if (pair.Value is ITreeAttribute) names.Add(pair.Key);
            }
            return names;
        }

        /// <summary>
        /// Replaces the whole carry root, keeping the revision counter moving. The client renders off
        /// that counter, so swapping the subtrees silently would leave the old block on screen.
        /// </summary>
        private static void WriteRoot(Entity entity, ITreeAttribute root)
        {
            var previous = entity.WatchedAttributes.GetTreeAttribute(CarriedAttribute);
            int revision = previous?.GetInt(RevisionKey, 0) ?? 0;

            var next = root ?? new TreeAttribute();
            next.SetInt(RevisionKey, revision + 1);
            entity.WatchedAttributes[CarriedAttribute] = next;
            entity.WatchedAttributes.MarkPathDirty(CarriedAttribute);
        }

        /// <summary>
        /// Undoes as much of a carried block as can be undone from the outside: the animation and the
        /// stat modifiers. The subtree itself goes with the <see cref="WriteRoot"/> that follows.
        /// </summary>
        private static void ClearSlotByHand(Entity entity, string slotName)
        {
            var slot = entity.WatchedAttributes.GetTreeAttribute(CarriedAttribute)?.GetTreeAttribute(slotName);
            var animation = slot?.GetString(AnimationKey);
            if (!string.IsNullOrEmpty(animation)) entity.StopAnimation(animation);

            string statCode = "carryon:" + slotName;
            foreach (var category in StatCategories) entity.Stats?.Remove(category, statCode);

            // CarryOn keeps a non-synced mirror of the slot; left behind, it desyncs the client's idea
            // of what is being carried.
            entity.Attributes?.GetTreeAttribute(CarriedAttribute)?.RemoveAttribute(slotName);
        }

        /// <summary>
        /// Fallback for when CarryOn's API cannot be reached: the block is back in the attributes and
        /// renders and drops like any other carried block, but its walkspeed/hunger penalty is not
        /// reapplied until the player picks it up again.
        /// </summary>
        private static void RestoreSlotByHand(Entity entity, string slotName)
        {
            var slot = entity.WatchedAttributes.GetTreeAttribute(CarriedAttribute)?.GetTreeAttribute(slotName);
            if (slot == null) return;

            // A stack that came out of ModData carries ids, not a collectible, until it is resolved.
            var stack = slot.GetItemstack(StackKey);
            if (stack != null && !stack.ResolveBlockOrItem(entity.World)) slot.RemoveAttribute(StackKey);

            var animation = slot.GetString(AnimationKey);
            if (!string.IsNullOrEmpty(animation)) entity.StartAnimation(animation);
        }

        /// <summary>
        /// Hands the hotbar back. Carrying in the Hands slot replaces both hand slots with CarryOn's
        /// LockedItemSlot, which refuses everything - a leftover lock silently swallows part of the
        /// match loadout. Recognised by type name and unwrapped through its own Original property, so
        /// this works without CarryOn's assembly.
        /// </summary>
        private static void UnlockHotbar(EntityPlayer entity)
        {
            var hotbar = entity?.Player?.InventoryManager?.GetHotbarInventory();
            if (hotbar == null) return;

            for (int i = 0; i < hotbar.Count; i++)
            {
                var slot = hotbar[i];
                if (slot == null || slot.GetType().Name != "LockedItemSlot") continue;

                if (slot.GetType().GetProperty("Original")?.GetValue(slot) is ItemSlot original)
                    hotbar[i] = original;
            }
        }

        // ---------------------------------------------------------------------------------------
        // CarryOn's own API, reached by reflection.
        // ---------------------------------------------------------------------------------------

        private static bool resolveAttempted;
        private static CarryManagerApi resolved;

        /// <summary>The live CarryManager, or null when CarryOn is absent or its API has moved. Resolved
        /// once per process: mods do not come and go at runtime.</summary>
        private static CarryManagerApi Resolve(ICoreAPI api)
        {
            if (resolveAttempted) return resolved;
            resolveAttempted = true;

            try
            {
                var system = api?.ModLoader?.GetModSystem("CarryOn.CarrySystem");
                var manager = system?.GetType().GetProperty("CarryManager")?.GetValue(system);
                if (manager == null) return null;

                resolved = CarryManagerApi.Build(manager);
                if (resolved == null)
                    api.World?.Logger?.Warning("[canevents] CarryOn is installed but its carry API is not the "
                        + "one this mod expects; carried blocks will be handled by attribute only.");
            }
            catch (Exception e)
            {
                api?.World?.Logger?.Warning("[canevents] Could not reach CarryOn's carry API: {0}", e.Message);
            }
            return resolved;
        }

        /// <summary>The three CarryManager methods this shim needs, bound to their enum slot type.</summary>
        private sealed class CarryManagerApi
        {
            private object manager;
            private Type slotType;
            private MethodInfo getCarried;
            private MethodInfo setCarried;
            private MethodInfo removeCarried;

            /// <summary>Binds against whatever the installed CarryOn exposes, or null if a piece is
            /// missing - matched on shape (an entity plus a carry-slot enum) rather than on exact
            /// signatures, since the optional parameters have changed between versions.</summary>
            public static CarryManagerApi Build(object manager)
            {
                var api = new CarryManagerApi { manager = manager };

                foreach (var method in manager.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    var ps = method.GetParameters();
                    if (ps.Length < 2 || !typeof(Entity).IsAssignableFrom(ps[0].ParameterType)) continue;

                    if (method.Name == "GetCarried" && ps.Length == 2 && ps[1].ParameterType.IsEnum)
                    {
                        api.getCarried = method;
                        api.slotType = ps[1].ParameterType;
                    }
                    else if (method.Name == "RemoveCarried" && ps[1].ParameterType.IsEnum)
                    {
                        api.removeCarried = method;
                    }
                    else if (method.Name == "SetCarried" && ps.Length >= 3)
                    {
                        api.setCarried = method;
                    }
                }

                return api.getCarried != null && api.setCarried != null && api.removeCarried != null ? api : null;
            }

            /// <summary>RemoveCarried for one slot. False when the call could not be made, in which case
            /// the caller falls back to editing the attributes itself.</summary>
            public bool Remove(Entity entity, string slotName)
                => TryParseSlot(slotName, out var slot) && Invoke(removeCarried, entity, slot);

            /// <summary>
            /// Re-seats what the attributes already say is in this slot, so CarryOn reapplies the
            /// effects that ride along with it. False when the slot could not be read back.
            /// </summary>
            public bool Reapply(Entity entity, string slotName)
            {
                if (!TryParseSlot(slotName, out var slot)) return false;

                object carried = null;
                if (!Invoke(getCarried, out carried, entity, slot) || carried == null) return false;

                // Third argument is CarryOn's "overrideSlot": the slot we just read from, so a version
                // whose CarriedBlock does not remember its own slot still puts it back in the right one.
                return Invoke(setCarried, entity, carried, slot);
            }

            private bool TryParseSlot(string slotName, out object slot)
            {
                slot = null;
                if (slotType == null || !Enum.IsDefined(slotType, slotName)) return false;
                slot = Enum.Parse(slotType, slotName);
                return true;
            }

            private bool Invoke(MethodInfo method, params object[] leading)
                => Invoke(method, out _, leading);

            /// <summary>
            /// Calls a CarryManager method, filling in whatever optional parameters this version of it
            /// declares from their own declared defaults. False if it threw - the caller then handles
            /// the slot by hand rather than leaving it half-done.
            /// </summary>
            private bool Invoke(MethodInfo method, out object result, params object[] leading)
            {
                result = null;

                var ps = method.GetParameters();
                var args = new object[ps.Length];
                for (int i = 0; i < ps.Length; i++)
                    args[i] = i < leading.Length ? leading[i]
                            : ps[i].HasDefaultValue ? ps[i].DefaultValue
                            : null;

                try
                {
                    result = method.Invoke(manager, args);
                    return true;
                }
                catch (Exception e)
                {
                    (leading[0] as Entity)?.Api?.World?.Logger?.Warning(
                        "[canevents] CarryOn.{0} failed: {1}", method.Name, (e.InnerException ?? e).Message);
                    return false;
                }
            }
        }
    }
}
