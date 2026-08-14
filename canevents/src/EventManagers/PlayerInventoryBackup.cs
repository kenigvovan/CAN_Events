using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace canevents.src.EventManagers
{
    // Single owner of the per-player event inventory/health backup, persisted to ModData so it
    // survives a disconnect or crash. BOTH restore paths (normal end-of-event and crash/reconnect) go
    // through Restore() here, so the clear-then-restore + MaxHealth-recompute logic can't drift - it
    // previously did, leaking the loadout and a stale buffed max-health on crash.
    public static class PlayerInventoryBackup
    {
        public const string ModDataKey = "canevents_inventory_backup";

        /// <summary>
        /// Where a snapshot goes when restoring it threw. Kept rather than deleted: by then the event
        /// loadout has already been stripped, so the blob is the only copy of the player's real items
        /// left anywhere. An admin can pull it out of ModData; deleting it loses them for good.
        /// </summary>
        public const string FailedModDataKey = "canevents_inventory_backup_failed";

        /// <summary>
        /// True for a slot that only PROJECTS the contents of a worn bag. The items themselves live
        /// inside that bag's own ItemStack, which is snapshotted from whatever slot holds the bag, so
        /// copying these slots as well hands every carried item back twice. Vanilla backpacks work
        /// this way, and so does every mod bag built on the same IHeldBag API (quivers, sheaths and
        /// pouches from QuiversAndSheaths) - those put the bag in the gear inventory while its content
        /// slots appear in the backpack inventory, which is why the two can never be treated as one
        /// flat list of slots.
        /// </summary>
        private static bool IsBagContent(ItemSlot slot) => slot is ItemSlotBagContent;

        /// <summary>
        /// True if this stack makes OTHER slots exist or function: a bag (its content slots appear in
        /// the backpack inventory), or a slot enabler like a QuiversAndSheaths belt / back sling.
        /// A belt is NOT a bag - it holds nothing itself, it only enables the addBelt* gear slots the
        /// sheaths hang in (CombatOverhaul's IEnableAdditionalSlots, recognised here without a CO
        /// reference by its vanilla clothescategory) - but it must come back in the same early pass:
        /// a sheath announced before its belt sits in a disabled slot, which CombatOverhaul answers
        /// with a force-drop into the single ground slot that re-enters itself until the stack
        /// overflows. Uncatchable, so the order of restore passes is what prevents it.
        /// </summary>
        private static bool IsBagOrSlotEnabler(ItemStack stack)
        {
            if (stack?.Collectible == null) return false;
            if (stack.Collectible.GetCollectibleInterface<IHeldBag>() != null) return true;
            return Compat.GearSlotOrderCompat.IsSlotEnabler(stack);
        }

        /// <summary>Snapshot the player's current inventory + health to ModData. Call right before
        /// clearing their inventory and handing out the event loadout.</summary>
        /// <param name="gearStaysWithPlayer">
        /// The player keeps whatever they end the match with, so the snapshot records their state but
        /// must NEVER put items back - restoring would duplicate everything that dropped on death. The
        /// flag is stored inside the snapshot because crash recovery reads it without knowing the mode.
        /// </param>
        public static void Save(IServerPlayer player, ICoreServerAPI sapi, bool gearStaysWithPlayer = false)
        {
            // NEVER overwrite an existing backup. One is only present here if a prior event didn't
            // restore it, in which case the current inventory is the event loadout, not the player's
            // real belongings - overwriting would bury the real items for good.
            var existing = player.GetModdata(ModDataKey);
            if (existing != null && existing.Length > 0)
            {
                sapi.World.Logger.Warning(
                    "PlayerInventoryBackup: {0} already has an inventory backup; keeping it instead of "
                    + "overwriting, to avoid losing their real items.", player.PlayerName);
                return;
            }

            var tree = new TreeAttribute();
            int slotIndex = 0;
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
                    // Skipped, not saved: their contents ride along inside the bag's own stack.
                    if (IsBagContent(slot)) continue;
                    // Clone: the live stack can be mutated to zero-size if the player drops it during
                    // the countdown, which would restore a ghost stack.
                    if (slot?.Itemstack != null) invTree.SetItemstack($"slot_{i}", slot.Itemstack.Clone());
                }
                tree[$"inv_{slotIndex}"] = invTree;
                slotIndex++;
            }
            tree.SetInt("invCount", slotIndex);
            if (gearStaysWithPlayer) tree.SetBool("keepLiveInventory", true);

            var health = player.Entity.GetBehavior<EntityBehaviorHealth>();
            if (health != null)
            {
                tree.SetFloat("health", health.Health);
                tree.SetFloat("maxHealth", health.MaxHealth);
            }

            // An event tops the player up to full saturation, so without a restore the match doubles as
            // a free meal. The nutrient levels ride along because they are not cosmetic: they feed
            // UpdateNutrientHealthBoost, which drives the "nutrientHealthMod" max-health modifier, and
            // every vanilla revive halves them - a respawn mode would cost permanent max health.
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
            sapi.World.Logger.Notification("Saved event inventory for {0}", player.PlayerName);
        }

        /// <summary>Wipe every non-creative inventory slot. Used before handing out a loadout, on
        /// respawn / lethal hit (so default items don't reappear), and inside Restore().</summary>
        /// <summary>
        /// Drops the pieces of kit that are meant to outlive their owner (see
        /// IssuedGear.DropOnDeathKey) before the rest is swept up. Call from the death path, ahead of
        /// <see cref="ClearInventory"/>: by the time the engine would drop anything, the inventory is
        /// already empty, so a weapon that is supposed to be lootable has to be put on the ground here.
        /// </summary>
        public static int DropDeathDrops(IServerPlayer player, ICoreServerAPI sapi)
        {
            if (player?.Entity == null || sapi == null) return 0;

            int dropped = 0;
            foreach (var inv in player.InventoryManager.Inventories.Values.ToArray())
            {
                if (inv.ClassName == "creative") continue;
                for (int i = 0; i < inv.Count; i++)
                {
                    var slot = inv[i];
                    var stack = slot?.Itemstack;
                    if (stack == null || IsBagContent(slot)) continue;
                    if (!eventBehavior.Shared.IssuedGear.DropsOnDeath(stack)) continue;

                    slot.Itemstack = null;
                    slot.MarkDirty();
                    sapi.World.SpawnItemEntity(stack, player.Entity.Pos.XYZ);
                    dropped++;
                }
            }
            return dropped;
        }

        public static void ClearInventory(IServerPlayer player)
        {
            // Emptied silently first, notified second - the split is load-bearing, not tidiness.
            //
            // Notifying a gear slot mid-clear can hang the server for good. CombatOverhaul's
            // ArmorInventory answers a modified gear slot by dropping the contents of every gear slot
            // that just lost its parent (take off a belt and the sheaths hanging on it are orphaned)
            // into the single ground-inventory slot, then marking that slot dirty - which re-enters the
            // same handler. It only terminates once the slot is actually empty, so the second orphaned
            // bag, arriving at an already-occupied ground slot, moves nothing and recurses until the
            // stack overflows. A StackOverflowException cannot be caught: the process dies outright.
            //
            // Clearing everything before the first MarkDirty means that by the time the handler runs
            // there is nothing left for it to drop, so it never re-enters.
            //
            // Bag content slots are skipped throughout: their items are already gone once the bag
            // itself is, and nulling the projection by hand leaves the slot list and the bag's own
            // stack disagreeing.
            var emptied = new List<(ItemSlot slot, ItemStack removed)>();

            foreach (var inv in player.InventoryManager.Inventories.Values.ToArray())
            {
                if (inv.ClassName == "creative") continue;
                for (int i = 0; i < inv.Count; i++)
                {
                    var slot = inv[i];
                    if (slot?.Itemstack == null) continue;
                    if (IsBagContent(slot)) continue;
                    emptied.Add((slot, slot.Itemstack));
                    slot.Itemstack = null;
                }
            }

            // Dependents-first: the client must never apply "belt removed" while a sheath packet is
            // still in flight behind it - see GearSlotOrderCompat for the crash that causes.
            Compat.GearSlotOrderCompat.MarkDirtyAfterClear(emptied);
        }

        /// <summary>
        /// Puts one saved stack back into the slot it came from and returns that slot, so the caller
        /// can notify it once the whole pass has landed - see ClearInventory for why a gear slot must
        /// never be marked dirty while the inventory is still half-assembled. Returns null when the
        /// stack was not placed. Refuses (and logs) rather than forcing it elsewhere: the ways a slot
        /// can be unusable all mean the item is either already accounted for or does not belong here,
        /// and shoving it into the next free slot would mint the second copy this method prevents.
        /// </summary>
        private static ItemSlot PutBack(IServerPlayer player, ICoreServerAPI sapi, string invId, int slotIndex, ItemStack stack)
        {
            IInventory targetInv = null;
            foreach (var inv in player.InventoryManager.Inventories.Values)
                if (inv.InventoryID == invId) { targetInv = inv; break; }

            if (targetInv == null)
            {
                sapi.World.Logger.Warning("PlayerInventoryBackup {0}: inventory '{1}' missing, '{2}' x{3} not restored",
                    player.PlayerName, invId, stack.Collectible?.Code?.ToString() ?? "<null>", stack.StackSize);
                return null;
            }

            // Out of range, or the index now belongs to a bag's content projection. The latter is the
            // normal outcome for a snapshot taken before this dupe fix: those stacks were saved twice,
            // and the copy inside the bag has already come back with the bag itself in the first pass.
            if (slotIndex >= targetInv.Count || IsBagContent(targetInv[slotIndex]))
            {
                sapi.World.Logger.Notification("PlayerInventoryBackup {0}: inv '{1}' slot {2} is not a plain slot anymore, '{3}' x{4} left to the bag that owns it",
                    player.PlayerName, invId, slotIndex, stack.Collectible?.Code?.ToString() ?? "<null>", stack.StackSize);
                return null;
            }

            var slot = targetInv[slotIndex];
            if (slot == null) return null;
            if (slot.Itemstack != null)
            {
                sapi.World.Logger.Warning("PlayerInventoryBackup {0}: inv '{1}' slot {2} already holds '{3}', '{4}' x{5} not restored",
                    player.PlayerName, invId, slotIndex, slot.Itemstack.Collectible?.Code?.ToString() ?? "<null>",
                    stack.Collectible?.Code?.ToString() ?? "<null>", stack.StackSize);
                return null;
            }

            slot.Itemstack = stack;
            return slot;
        }

        /// <summary>
        /// Restores from ModData if present: revive if dead, strip the event loadout, put back the
        /// saved items, restore health (MarkDirty recomputes MaxHealth from the entity's CURRENT
        /// modifiers, so a buff whose inflated maxhealth was autosaved during a crash drops cleanly),
        /// then clear the backup. False when there is no backup. Does NOT message the player.
        /// </summary>
        public static bool Restore(IServerPlayer player, ICoreServerAPI sapi)
        {
            byte[] data = player.GetModdata(ModDataKey);
            if (data == null || data.Length == 0) return false;

            try
            {
                var tree = new TreeAttribute();
                tree.FromBytes(data);

                if (!player.Entity.Alive) player.Entity.Revive();

                // A match played with the players' own gear hands nothing back: putting the snapshot
                // back would mint a copy of everything that dropped.
                bool keepLive = tree.GetBool("keepLiveInventory", false);

                // Strip the event loadout first: the per-slot restore below only overwrites slots that
                // held an item pre-event, so gear issued into then-empty slots would otherwise survive.
                if (!keepLive) ClearInventory(player);

                // Flattened first, then replayed bags-first. A bag restored into its slot makes its
                // content slots reappear - in the backpack inventory even when the bag itself sits in
                // the gear inventory - so replaying inventory-by-inventory would write into a slot
                // layout that the very next inventory is about to change. Enumeration order of
                // InventoryManager.Inventories is not guaranteed either, so the pass order is the only
                // thing keeping this deterministic.
                int invCount = keepLive ? 0 : tree.GetInt("invCount", 0);
                var pending = new List<(string invId, int slotIndex, ItemStack stack)>();
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
                            sapi.World.Logger.Warning("PlayerInventoryBackup {0}: inv '{1}' slot {2}: collectible '{3}' unresolved, left empty", player.PlayerName, invId, i, stack.Collectible?.Code?.ToString() ?? "<null>");
                            continue;
                        }
                        pending.Add((invId, i, stack));
                    }
                }

                // Bags land first so that by the time ordinary items are placed, the content slots
                // their bags own already exist and can be recognised (and skipped) instead of being
                // written into. Within a pass every stack is seated before anything is marked dirty:
                // a bag notified while its parent - a belt, say - is still missing counts as an
                // orphaned gear slot, which sends CombatOverhaul into the unbounded recursion
                // described in ClearInventory.
                for (int pass = 0; pass < 2; pass++)
                {
                    bool bagPass = pass == 0;
                    var placed = new List<ItemSlot>();
                    foreach (var entry in pending)
                    {
                        if (IsBagOrSlotEnabler(entry.stack) != bagPass) continue;
                        var slot = PutBack(player, sapi, entry.invId, entry.slotIndex, entry.stack);
                        if (slot != null) placed.Add(slot);
                    }
                    // Parents-first; a dependent whose parent did not make it back is handed over
                    // as a plain give rather than announced - see GearSlotOrderCompat.
                    Compat.GearSlotOrderCompat.MarkDirtyAfterFill(placed, stack =>
                    {
                        sapi.World.Logger.Warning(
                            "PlayerInventoryBackup {0}: '{1}' x{2} was restored into a gear slot whose parent is missing; given normally instead.",
                            player.PlayerName, stack.Collectible?.Code?.ToString() ?? "<null>", stack.StackSize);
                        InventoryUtil.Give(sapi, player, stack, "restore");
                    });
                }

                // Must precede the health block: UpdateNutrientHealthBoost rewrites the
                // "nutrientHealthMod" modifier from the restored levels, so MarkDirty below recomputes
                // MaxHealth off the pre-event nutrition, not off what the in-match revives left behind.
                // A missing subtree is a backup written before hunger was tracked; leave hunger alone.
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
                return true;
            }
            catch (System.Exception e)
            {
                // A failed restore must not strand the player in a retry loop - but it must not throw
                // their belongings away either. The blob is parked under a second key: the loadout is
                // already stripped by this point, so this is the only copy of their real inventory.
                sapi.World.Logger.Error(
                    "PlayerInventoryBackup.Restore for {0}: {1} - snapshot kept under '{2}' in their "
                    + "ModData for manual recovery.", player.PlayerName, e.Message, FailedModDataKey);
                try
                {
                    player.SetModdata(FailedModDataKey, data);
                    player.RemoveModdata(ModDataKey);
                }
                catch (System.Exception parkError)
                {
                    // Parking failed too; keep the original rather than losing both.
                    sapi.World.Logger.Error("PlayerInventoryBackup: could not park the failed snapshot for {0}: {1}",
                        player.PlayerName, parkError.Message);
                }
                return false;
            }
        }
    }
}
