using System;
using System.Collections.Generic;
using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace canevents.src.Compat
{
    // CarryOn keeps carried blocks in WatchedAttributes["carryon:Carried"], not in any inventory,
    // so we take them off for the match and put them back with the inventory backup.
    // No assembly reference: CarryManager via reflection, falls back to raw attributes.
    public static class CarryOnCompat
    {
        // Same key in CarryOn 1.x and 2.x.
        public const string CarriedAttribute = "carryon:Carried";

        // Revision counter next to the slot subtrees; the client re-renders when it changes.
        private const string RevisionKey = "Rev";

        private const string AnimationKey = "Animation";

        private const string StackKey = "Stack";

        // Stat code is "carryon:<Slot>".
        private static readonly string[] StatCategories = { "walkspeed", "hungerrate" };

        public static bool IsCarrying(Entity entity) => SlotNames(entity).Count > 0;

        // Detached copy, safe to serialize into ModData. Null if nothing is carried.
        public static ITreeAttribute Snapshot(Entity entity)
        {
            var carried = entity?.WatchedAttributes?.GetTreeAttribute(CarriedAttribute);
            if (carried == null || SlotNames(entity).Count == 0) return null;
            return carried.Clone() as ITreeAttribute;
        }

        // Discards carried blocks without dropping them. Snapshot first if they should come back.
        public static int Clear(IServerPlayer player) => Clear(player?.Entity);

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

            // By-hand clear leaves the subtree; replace the root so the client stops rendering it.
            if (SlotNames(entity).Count > 0) WriteRoot(entity, null);

            UnlockHotbar(entity as EntityPlayer);
            return slots.Count;
        }

        public static void Restore(IServerPlayer player, ITreeAttribute snapshot) => Restore(player?.Entity, snapshot);

        public static void Restore(Entity entity, ITreeAttribute snapshot)
        {
            if (entity == null || snapshot == null) return;

            // Drop anything picked up during the match; the snapshot wins.
            Clear(entity);

            if (!(snapshot.Clone() is ITreeAttribute restored)) return;
            WriteRoot(entity, restored);

            var slots = SlotNames(entity);
            if (slots.Count == 0) return;

            var carryApi = Resolve(entity.Api);
            foreach (var slotName in slots)
            {
                // Get+Set through CarryOn reapplies stats, animation and hotbar locks.
                if (carryApi != null && carryApi.Reapply(entity, slotName)) continue;
                RestoreSlotByHand(entity, slotName);
            }
        }

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

        // Must bump Rev, otherwise the client keeps rendering the old block.
        private static void WriteRoot(Entity entity, ITreeAttribute root)
        {
            var previous = entity.WatchedAttributes.GetTreeAttribute(CarriedAttribute);
            int revision = previous?.GetInt(RevisionKey, 0) ?? 0;

            var next = root ?? new TreeAttribute();
            next.SetInt(RevisionKey, revision + 1);
            entity.WatchedAttributes[CarriedAttribute] = next;
            entity.WatchedAttributes.MarkPathDirty(CarriedAttribute);
        }

        private static void ClearSlotByHand(Entity entity, string slotName)
        {
            var slot = entity.WatchedAttributes.GetTreeAttribute(CarriedAttribute)?.GetTreeAttribute(slotName);
            var animation = slot?.GetString(AnimationKey);
            if (!string.IsNullOrEmpty(animation)) entity.StopAnimation(animation);

            string statCode = "carryon:" + slotName;
            foreach (var category in StatCategories) entity.Stats?.Remove(category, statCode);

            // CarryOn's non-synced mirror of the slot; leaving it desyncs the client.
            entity.Attributes?.GetTreeAttribute(CarriedAttribute)?.RemoveAttribute(slotName);
        }

        // Fallback without CarryOn's API: walkspeed/hunger penalty is not reapplied.
        private static void RestoreSlotByHand(Entity entity, string slotName)
        {
            var slot = entity.WatchedAttributes.GetTreeAttribute(CarriedAttribute)?.GetTreeAttribute(slotName);
            if (slot == null) return;

            // Stack from ModData needs resolving.
            var stack = slot.GetItemstack(StackKey);
            if (stack != null && !stack.ResolveBlockOrItem(entity.World)) slot.RemoveAttribute(StackKey);

            var animation = slot.GetString(AnimationKey);
            if (!string.IsNullOrEmpty(animation)) entity.StartAnimation(animation);
        }

        // CarryOn swaps hand slots for LockedItemSlot, which rejects everything. Unwrap via Original.
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

        private static bool resolveAttempted;
        private static CarryManagerApi resolved;

        // Resolved once per process.
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

        private sealed class CarryManagerApi
        {
            private object manager;
            private Type slotType;
            private MethodInfo getCarried;
            private MethodInfo setCarried;
            private MethodInfo removeCarried;

            // Matched by shape, not exact signature: optional params changed between versions.
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

            public bool Remove(Entity entity, string slotName)
                => TryParseSlot(slotName, out var slot) && Invoke(removeCarried, entity, slot);

            public bool Reapply(Entity entity, string slotName)
            {
                if (!TryParseSlot(slotName, out var slot)) return false;

                object carried = null;
                if (!Invoke(getCarried, out carried, entity, slot) || carried == null) return false;

                // overrideSlot: older CarriedBlock doesn't remember its slot.
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

            // Fills trailing optional params with their declared defaults.
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
