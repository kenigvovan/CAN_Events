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
        public static void ClearInventory(IServerPlayer player)
        {
            foreach (var inv in player.InventoryManager.Inventories.Values)
            {
                if (inv.ClassName == "creative") continue;
                for (int i = 0; i < inv.Count; i++)
                {
                    if (inv[i]?.Itemstack != null) { inv[i].Itemstack = null; inv[i].MarkDirty(); }
                }
            }
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

                int invCount = keepLive ? 0 : tree.GetInt("invCount", 0);
                for (int idx = 0; idx < invCount; idx++)
                {
                    var invTree = tree[$"inv_{idx}"] as TreeAttribute;
                    if (invTree == null) continue;

                    string invId = invTree.GetString("invId");
                    int slotCount = invTree.GetInt("slotCount", 0);

                    IInventory targetInv = null;
                    foreach (var inv in player.InventoryManager.Inventories.Values)
                        if (inv.InventoryID == invId) { targetInv = inv; break; }

                    if (targetInv == null)
                    {
                        sapi.World.Logger.Warning("PlayerInventoryBackup {0}: inventory '{1}' missing - {2} slot(s) lost", player.PlayerName, invId, slotCount);
                        continue;
                    }
                    if (slotCount > targetInv.Count)
                        sapi.World.Logger.Warning("PlayerInventoryBackup {0}: inv '{1}' shrank ({2} saved -> {3} current); trailing slots dropped", player.PlayerName, invId, slotCount, targetInv.Count);

                    for (int i = 0; i < slotCount && i < targetInv.Count; i++)
                    {
                        ItemStack stack = invTree.GetItemstack($"slot_{i}");
                        if (stack == null) continue;
                        if (!stack.ResolveBlockOrItem(sapi.World))
                        {
                            sapi.World.Logger.Warning("PlayerInventoryBackup {0}: inv '{1}' slot {2}: collectible '{3}' unresolved, left empty", player.PlayerName, invId, i, stack.Collectible?.Code?.ToString() ?? "<null>");
                            continue;
                        }
                        targetInv[i].Itemstack = stack;
                        targetInv[i].MarkDirty();
                    }
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
                // A corrupt blob must not strand the player: log, drop it, move on.
                sapi.World.Logger.Error("PlayerInventoryBackup.Restore for {0}: {1}", player.PlayerName, e.Message);
                player.RemoveModdata(ModDataKey);
                return false;
            }
        }
    }
}
