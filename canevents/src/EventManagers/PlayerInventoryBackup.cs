using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace canevents.src.EventManagers
{
    // Per-player inventory/health backup in ModData. Both the normal and the crash/reconnect restore go through Restore().
    public static partial class PlayerInventoryBackup
    {
        public const string ModDataKey = "canevents_inventory_backup";

        // Routine taken/returned lines, can be switched off.
        private static Log Audit(ICoreServerAPI sapi)
            => Log.For(InventoryAuditLog.Enabled ? sapi?.World?.Logger : null, "inventory");

        // Failures, never silenced.
        private static Log AuditAlways(ICoreServerAPI sapi) => Log.For(sapi?.World?.Logger, "inventory");

        // Snapshot parked here when Restore threw; loadout is already stripped by then, so it's the only copy.
        public const string FailedModDataKey = "canevents_inventory_backup_failed";

        // Bag content slots only project the bag's own stack; copying them too would dupe the items.
        private static bool IsBagContent(ItemSlot slot) => slot is ItemSlotBagContent;

        // Bag or slot enabler (QuiversAndSheaths belt). Must be restored before dependents, or CombatOverhaul recurses into a stack overflow.
        private static bool IsBagOrSlotEnabler(ItemStack stack)
        {
            if (stack?.Collectible == null) return false;
            if (stack.Collectible.GetCollectibleInterface<IHeldBag>() != null) return true;
            return Compat.GearSlotOrderCompat.IsSlotEnabler(stack);
        }

        // Player inventory with RemoveOnClose=true (e.g. PicoTrashSlot): drops out of InventoryManager when closed,
        // so we can't snapshot it. Locked for the match by HarmonyPatches.PersonalStashPatch.
        public static bool IsVanishingStash(IInventory inv)
            => inv is InventoryBasePlayer && inv.RemoveOnClose;

        // Iterates a copy of the inventory list, so callers may empty slots as they go.
        private static IEnumerable<ItemSlot> OwnSlots(IServerPlayer player)
        {
            foreach (var inv in player.InventoryManager.Inventories.Values.ToArray())
            {
                if (inv.ClassName == "creative") continue;
                for (int i = 0; i < inv.Count; i++)
                {
                    var slot = inv[i];
                    if (slot == null || IsBagContent(slot)) continue;
                    yield return slot;
                }
            }
        }

        // gearStaysWithPlayer: snapshot is a record only, never restored (would dupe death drops).
        // Returns false if a backup already exists; caller must not clear the inventory then.
        public static bool Save(IServerPlayer player, ICoreServerAPI sapi, bool gearStaysWithPlayer = false,
            string eventCode = null)
        {
            // Existing backup means a previous restore didn't happen; current inventory is a loadout, not real items.
            var existing = player.GetModdata(ModDataKey);
            if (existing != null && existing.Length > 0)
            {
                AuditAlways(sapi).Warning(
                    "refused to overwrite: {0} already has an inventory backup (event '{1}'); keeping it "
                    + "instead of overwriting, to avoid losing their real items. Their CURRENT inventory "
                    + "is not saved anywhere - do not clear it.",
                    player.PlayerName, eventCode ?? "?");
                InventoryAuditLog.Record(sapi, InventoryAuditKind.RefusedOverwrite, player.PlayerUID,
                    player.PlayerName, eventCode, 0,
                    "an unsettled snapshot was already there; the live inventory was not saved");
                return false;
            }

            var tree = new TreeAttribute();
            int slotIndex = 0;
            int stackCount = 0;
            foreach (var inv in player.InventoryManager.Inventories.Values)
            {
                if (inv.ClassName == "creative") continue;

                var invTree = new TreeAttribute();
                invTree.SetString("className", inv.ClassName);
                invTree.SetString("invId", inv.InventoryID);
                invTree.SetInt("slotCount", inv.Count);
                for (int i = 0; i < inv.Count; i++)
                {
                    var slot = inv[i];
                    if (IsBagContent(slot)) continue;
                    // Clone: dropping it during the countdown zeroes the live stack.
                    if (slot?.Itemstack != null)
                    {
                        invTree.SetItemstack($"slot_{i}", slot.Itemstack.Clone());
                        stackCount++;
                    }
                }
                tree[$"inv_{slotIndex}"] = invTree;
                slotIndex++;
            }
            tree.SetInt("invCount", slotIndex);
            if (gearStaysWithPlayer) tree.SetBool("keepLiveInventory", true);

            tree.SetLong("savedAt", UnixTime.Now);
            tree.SetString("eventCode", eventCode ?? "");
            tree.SetInt("stackCount", stackCount);

            // CarryOn blocks live on the entity, not in an inventory.
            var carried = Compat.CarryOnCompat.Snapshot(player.Entity);
            if (carried != null) tree["carryon"] = carried;

            // Stat modifiers other mods derived from the inventory we're about to empty.
            if (!gearStaysWithPlayer)
            {
                var stats = Compat.EntityStatsGuard.Snapshot(player.Entity);
                if (stats != null) tree["stats"] = stats;
            }

            var health = player.Entity.GetBehavior<EntityBehaviorHealth>();
            if (health != null)
            {
                tree.SetFloat("health", health.Health);
                tree.SetFloat("maxHealth", health.MaxHealth);
            }

            // Nutrients feed "nutrientHealthMod" max health, and every vanilla revive halves them.
            var hunger = player.Entity.GetBehavior<EntityBehaviorHunger>();
            if (hunger != null)
            {
                var hungerTree = new TreeAttribute();
                hungerTree.SetFloat("saturation", hunger.Saturation);
                hungerTree.SetFloat("fruit", hunger.FruitLevel);
                hungerTree.SetFloat("vegetable", hunger.VegetableLevel);
                hungerTree.SetFloat("protein", hunger.ProteinLevel);
                hungerTree.SetFloat("grain", hunger.GrainLevel);
                hungerTree.SetFloat("dairy", hunger.DairyLevel);
                tree["hunger"] = hungerTree;
            }

            player.SetModdata(ModDataKey, tree.ToBytes());

            Audit(sapi).Notification("taken: {0} - {1} stack(s) into a snapshot for event '{2}'{3}",
                player.PlayerName, stackCount, eventCode ?? "?",
                gearStaysWithPlayer ? " (own-gear match: recorded only, never restored)" : "");
            InventoryAuditLog.Record(sapi, InventoryAuditKind.Taken, player.PlayerUID, player.PlayerName,
                eventCode, stackCount,
                gearStaysWithPlayer ? "own-gear match, snapshot is a record only" : null);
            return true;
        }

        // Drops IssuedGear.DropOnDeathKey items. Call before ClearInventory; the engine drop sees an empty inventory.
        public static int DropDeathDrops(IServerPlayer player, ICoreServerAPI sapi)
        {
            if (player?.Entity == null || sapi == null) return 0;

            int dropped = 0;
            foreach (var slot in OwnSlots(player))
            {
                var stack = slot.Itemstack;
                if (stack == null) continue;
                if (!eventBehavior.Shared.IssuedGear.DropsOnDeath(stack)) continue;

                slot.Itemstack = null;
                slot.MarkDirty();
                sapi.World.SpawnItemEntity(stack, player.Entity.Pos.XYZ);
                dropped++;
            }
            return dropped;
        }

        // Copies of carried stacks that match. Bag contents count too: ClearInventory takes them with the bag.
        public static List<ItemStack> CloneCarried(IServerPlayer player, System.Func<ItemStack, bool> match)
        {
            var result = new List<ItemStack>();
            if (player?.InventoryManager == null) return result;

            foreach (var inv in player.InventoryManager.Inventories.Values.ToArray())
            {
                if (inv.ClassName == "creative") continue;
                for (int i = 0; i < inv.Count; i++)
                {
                    var stack = inv[i]?.Itemstack;
                    if (stack == null || !match(stack)) continue;
                    // Already inside a bag that is taken whole.
                    if (inv[i] is ItemSlotBagContent content && content.BagIndex < inv.Count
                        && inv[content.BagIndex]?.Itemstack is ItemStack bag && match(bag)) continue;
                    result.Add(stack.Clone());
                }
            }
            return result;
        }

        // Diagnostic: non-zero at engine drop time means the death sweep missed something.
        public static int CountCarried(IServerPlayer player, out string summary)
        {
            summary = null;
            if (player?.InventoryManager == null) return 0;

            int count = 0;
            var codes = new List<string>();
            foreach (var slot in OwnSlots(player))
            {
                var stack = slot.Itemstack;
                if (stack == null) continue;
                count++;
                if (codes.Count < 12) codes.Add(stack.Collectible?.Code?.ToString() ?? "<null>");
            }

            if (count > 0)
                summary = string.Join(", ", codes) + (count > codes.Count ? ", ..." : "");
            return count;
        }

        public static void ClearInventory(IServerPlayer player)
        {
            // Empty everything before the first MarkDirty: CombatOverhaul's ArmorInventory drops orphaned
            // gear into one ground slot and re-enters itself, which overflows the stack if anything is left.
            var emptied = new List<(ItemSlot slot, ItemStack removed)>();

            foreach (var slot in OwnSlots(player))
            {
                if (slot.Itemstack == null) continue;
                emptied.Add((slot, slot.Itemstack));
                slot.Itemstack = null;
            }

            // Dependents first, see GearSlotOrderCompat.
            Compat.GearSlotOrderCompat.MarkDirtyAfterClear(emptied);

            // CarryOn back chest isn't in any inventory; otherwise it'd drop with contents on first death.
            Compat.CarryOnCompat.Clear(player);
        }

        // Returns the slot without MarkDirty (caller notifies after the pass). null + homeless=false: the stack
        // already came back inside its bag. null + homeless=true: nowhere to put it, caller must give it.
        private static ItemSlot PutBack(IServerPlayer player, ICoreServerAPI sapi, string invId, int slotIndex,
            ItemStack stack, out bool homeless)
        {
            homeless = false;

            IInventory targetInv = null;
            foreach (var inv in player.InventoryManager.Inventories.Values)
                if (inv.InventoryID == invId) { targetInv = inv; break; }

            if (targetInv == null)
            {
                // Usually a mod inventory missing this session.
                AuditAlways(sapi).Warning("{0}: inventory '{1}' missing, '{2}' x{3} handed over directly",
                    player.PlayerName, invId, stack.Collectible?.Code?.ToString() ?? "<null>", stack.StackSize);
                homeless = true;
                return null;
            }

            // Bag content slot: old snapshots saved these twice, the bag already brought them back.
            if (slotIndex >= targetInv.Count || IsBagContent(targetInv[slotIndex]))
            {
                Audit(sapi).Notification("{0}: inv '{1}' slot {2} is not a plain slot anymore, '{3}' x{4} left to the bag that owns it",
                    player.PlayerName, invId, slotIndex, stack.Collectible?.Code?.ToString() ?? "<null>", stack.StackSize);
                return null;
            }

            var slot = targetInv[slotIndex];
            if (slot == null) { homeless = true; return null; }
            if (slot.Itemstack != null)
            {
                // Filled since the clear (a mod re-equipping gear, a bag laying out contents).
                AuditAlways(sapi).Warning("{0}: inv '{1}' slot {2} already holds '{3}', '{4}' x{5} handed over directly",
                    player.PlayerName, invId, slotIndex, slot.Itemstack.Collectible?.Code?.ToString() ?? "<null>",
                    stack.Collectible?.Code?.ToString() ?? "<null>", stack.StackSize);
                homeless = true;
                return null;
            }

            slot.Itemstack = stack;
            return slot;
        }

        // False when there's no backup. Doesn't message the player.
        public static bool Restore(IServerPlayer player, ICoreServerAPI sapi)
        {
            byte[] data = player.GetModdata(ModDataKey);
            if (data == null || data.Length == 0) return false;

            // Slots already returned, so a mid-way failure parks only the rest.
            var settled = new HashSet<(int inv, int slot)>();

            try
            {
                var tree = new TreeAttribute();
                tree.FromBytes(data);

                if (!player.Entity.Alive) player.Entity.Revive();

                bool keepLive = tree.GetBool("keepLiveInventory", false);

                // Restore below only writes slots that had items, so the loadout must be stripped first.
                if (!keepLive) ClearInventory(player);

                // Flatten, then replay bags first: a restored bag adds content slots to the backpack inventory.
                int invCount = keepLive ? 0 : tree.GetInt("invCount", 0);
                var pending = new List<(int invIdx, string invId, int slotIndex, ItemStack stack)>();
                for (int idx = 0; idx < invCount; idx++)
                {
                    var invTree = tree[$"inv_{idx}"] as TreeAttribute;
                    if (invTree == null) continue;

                    string invId = invTree.GetString("invId");
                    int slotCount = invTree.GetInt("slotCount", 0);

                    for (int i = 0; i < slotCount; i++)
                    {
                        ItemStack stack = invTree.GetItemstack($"slot_{i}");
                        if (stack == null) continue;
                        if (!stack.ResolveBlockOrItem(sapi.World))
                        {
                            AuditAlways(sapi).Warning("{0}: inv '{1}' slot {2}: collectible '{3}' unresolved, left empty", player.PlayerName, invId, i, stack.Collectible?.Code?.ToString() ?? "<null>");
                            continue;
                        }
                        pending.Add((idx, invId, i, stack));
                    }
                }

                int placedTotal = 0;

                // Given after both passes; a give mid-pass would notify a half-assembled gear inventory.
                var homelessStacks = new List<((int inv, int slot) key, ItemStack stack)>();

                // Seat the whole pass before any MarkDirty (see ClearInventory).
                for (int pass = 0; pass < 2; pass++)
                {
                    bool bagPass = pass == 0;
                    var placed = new List<ItemSlot>();
                    foreach (var entry in pending)
                    {
                        if (IsBagOrSlotEnabler(entry.stack) != bagPass) continue;
                        var slot = PutBack(player, sapi, entry.invId, entry.slotIndex, entry.stack,
                            out bool homeless);
                        var key = (entry.invIdx, entry.slotIndex);
                        if (slot != null) { placed.Add(slot); placedTotal++; settled.Add(key); }
                        else if (homeless) homelessStacks.Add((key, entry.stack));
                        else settled.Add(key);   // already back inside the bag that owns the slot
                    }
                    // Parents first; a dependent without its parent gets a plain give.
                    Compat.GearSlotOrderCompat.MarkDirtyAfterFill(placed, stack =>
                    {
                        sapi.World.Logger.Warning(
                            "PlayerInventoryBackup {0}: '{1}' x{2} was restored into a gear slot whose parent is missing; given normally instead.",
                            player.PlayerName, stack.Collectible?.Code?.ToString() ?? "<null>", stack.StackSize);
                        InventoryUtil.Give(sapi, player, stack, "restore");
                    });
                }

                foreach (var (key, stack) in homelessStacks)
                {
                    InventoryUtil.Give(sapi, player, stack, "restore");
                    settled.Add(key);
                    placedTotal++;
                }
                if (homelessStacks.Count > 0)
                    AuditAlways(sapi).Warning(
                        "{0}: {1} stack(s) could not go back to their own slots and were handed over "
                        + "directly - check the lines above for which, and why.",
                        player.PlayerName, homelessStacks.Count);

                // After the inventory: CarryOn locks the hand slots.
                if (!keepLive && tree["carryon"] is ITreeAttribute carriedTree)
                    Compat.CarryOnCompat.Restore(player, carriedTree);

                // After inventory, before health: MaxHealth reads "maxhealthExtraPoints".
                if (!keepLive && tree["stats"] is ITreeAttribute statsTree)
                {
                    int fixedUp = Compat.EntityStatsGuard.Restore(player.Entity, statsTree);
                    if (fixedUp > 0)
                        sapi.World.Logger.Notification(
                            "PlayerInventoryBackup {0}: put {1} stat modifier(s) back to their pre-match "
                            + "values - some mod did not recompute its own after the match.",
                            player.PlayerName, fixedUp);
                }

                // Before health: UpdateNutrientHealthBoost rewrites "nutrientHealthMod" that MarkDirty reads.
                var hunger = player.Entity.GetBehavior<EntityBehaviorHunger>();
                if (hunger != null && tree["hunger"] is TreeAttribute hungerTree)
                {
                    hunger.Saturation = hungerTree.GetFloat("saturation", hunger.Saturation);
                    hunger.FruitLevel = hungerTree.GetFloat("fruit", hunger.FruitLevel);
                    hunger.VegetableLevel = hungerTree.GetFloat("vegetable", hunger.VegetableLevel);
                    hunger.ProteinLevel = hungerTree.GetFloat("protein", hunger.ProteinLevel);
                    hunger.GrainLevel = hungerTree.GetFloat("grain", hunger.GrainLevel);
                    hunger.DairyLevel = hungerTree.GetFloat("dairy", hunger.DairyLevel);
                    hunger.UpdateNutrientHealthBoost();
                }

                var health = player.Entity.GetBehavior<EntityBehaviorHealth>();
                if (health != null)
                {
                    health.MarkDirty();
                    health.Health = tree.GetFloat("health", health.MaxHealth);
                }

                player.RemoveModdata(ModDataKey);

                int expected = tree.GetInt("stackCount", pending.Count);
                string eventCode = tree.GetString("eventCode", "");
                long savedAt = tree.GetLong("savedAt", 0);
                string age = savedAt > 0 ? TimeText.Duration(UnixTime.Now - savedAt) : "unknown";

                if (keepLive)
                {
                    Audit(sapi).Notification(
                        "returned: {0} - nothing to give back, own-gear match '{1}' (snapshot taken {2} ago "
                        + "was a record only); backup cleared.", player.PlayerName, eventCode, age);
                    InventoryAuditLog.Record(sapi, InventoryAuditKind.Returned, player.PlayerUID,
                        player.PlayerName, eventCode, 0, "own-gear match, nothing to return");
                }
                else
                {
                    string line = "returned: {0} - {1} of {2} stack(s) put back from the '{3}' snapshot taken {4} ago";
                    if (placedTotal < expected)
                        AuditAlways(sapi).Warning(
                            line + "; the missing ones are logged above, each with its reason",
                            player.PlayerName, placedTotal, expected, eventCode, age);
                    else
                        Audit(sapi).Notification(line, player.PlayerName, placedTotal, expected, eventCode, age);

                    InventoryAuditLog.Record(sapi, InventoryAuditKind.Returned, player.PlayerUID,
                        player.PlayerName, eventCode, placedTotal,
                        placedTotal < expected
                            ? placedTotal + " of " + expected + " stacks, snapshot age " + age
                            : "snapshot age " + age);
                }
                return true;
            }
            catch (System.Exception e)
            {
                // Park the blob instead of retrying or dropping it.
                AuditAlways(sapi).Error(
                    "restore failed: {0} - {1}. Their items are NOT lost: the {3} stack slot(s) already "
                    + "back are kept, the rest is parked under '{2}' in their ModData. Hand it back with "
                    + "/eventadmin inv restore {0}.",
                    player.PlayerName, e.Message, FailedModDataKey, settled.Count);
                InventoryAuditLog.Record(sapi, InventoryAuditKind.RestoreFailed, player.PlayerUID,
                    player.PlayerName, null, 0, e.Message);
                try
                {
                    ParkFailed(player, sapi, data, settled);
                }
                catch (System.Exception parkError)
                {
                    // Active key still holds what's left.
                    sapi.World.Logger.Error("PlayerInventoryBackup: could not park the failed snapshot for {0}: {1}",
                        player.PlayerName, parkError.Message);
                }
                return false;
            }
        }

        // Appends to an earlier parked snapshot instead of overwriting it.
        // Trimmed copy goes to the active key first so a throw here can't hand anything out twice.
        private static void ParkFailed(IServerPlayer player, ICoreServerAPI sapi, byte[] data,
            HashSet<(int inv, int slot)> settled)
        {
            var failed = new TreeAttribute();
            failed.FromBytes(data);
            bool keepLive = failed.GetBool("keepLiveInventory", false);
            int remaining = keepLive ? 0 : StripSettled(failed, settled);
            player.SetModdata(ModDataKey, failed.ToBytes());

            byte[] existing = player.GetModdata(FailedModDataKey);
            bool hasParked = existing != null && existing.Length > 0;

            if (!keepLive && remaining == 0)
            {
                // Every stack is already back; only the tail (health, hunger, stats) failed.
                AuditAlways(sapi).Warning("{0}: every stack was back before the restore failed; nothing to park.",
                    player.PlayerName);
            }
            else if (!hasParked)
            {
                player.SetModdata(FailedModDataKey, failed.ToBytes());
            }
            else if (!keepLive)
            {
                var parked = new TreeAttribute();
                parked.FromBytes(existing);
                if (parked.GetBool("keepLiveInventory", false)) parked = failed;
                else AppendInventories(parked, failed);
                player.SetModdata(FailedModDataKey, parked.ToBytes());
            }
            player.RemoveModdata(ModDataKey);
        }

        private static int StripSettled(TreeAttribute snapshot, HashSet<(int inv, int slot)> settled)
        {
            int remaining = 0;
            int invCount = snapshot.GetInt("invCount", 0);
            for (int idx = 0; idx < invCount; idx++)
            {
                if (!(snapshot[$"inv_{idx}"] is TreeAttribute invTree)) continue;
                int slotCount = invTree.GetInt("slotCount", 0);
                for (int i = 0; i < slotCount; i++)
                {
                    string key = $"slot_{i}";
                    if (!invTree.HasAttribute(key)) continue;
                    if (settled != null && settled.Contains((idx, i))) invTree.RemoveAttribute(key);
                    else remaining++;
                }
            }
            snapshot.SetInt("stackCount", remaining);
            return remaining;
        }

        // Hand-back gives every stack instead of seating it, so the merged slot layout doesn't matter.
        private static void AppendInventories(TreeAttribute into, TreeAttribute from)
        {
            int baseIdx = into.GetInt("invCount", 0);
            int count = from.GetInt("invCount", 0);
            for (int idx = 0; idx < count; idx++)
                if (from[$"inv_{idx}"] is TreeAttribute invTree) into[$"inv_{baseIdx + idx}"] = invTree;
            into.SetInt("invCount", baseIdx + count);
            into.SetInt("stackCount", into.GetInt("stackCount", 0) + from.GetInt("stackCount", 0));

            string a = into.GetString("eventCode", ""), b = from.GetString("eventCode", "");
            if (b.Length > 0 && a != b) into.SetString("eventCode", a.Length > 0 ? a + ", " + b : b);
        }
    }
}
