using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace canevents.src.EventManagers.eventBehavior.Shared
{
    public static class LoadoutService
    {
        public static List<LoadoutSet> ParseFromAttributes(JsonObject attributes)
        {
            var result = new List<LoadoutSet>();

            if (attributes[BehaviorConfigKeys.LoadoutSets].Exists)
            {
                foreach (var setObj in attributes[BehaviorConfigKeys.LoadoutSets].AsArray())
                {
                    var set = new LoadoutSet { Name = setObj["name"].AsString("Default") };
                    if (setObj["items"].Exists)
                    {
                        foreach (var item in setObj["items"].AsArray())
                        {
                            // Upgrades work inline too, not only from the loadout editor; they need an id.
                            var upgrades = item["upgrades"].Exists ? item["upgrades"].AsObject<List<UpgradeStep>>() : null;
                            set.Items.Add(new LoadoutEntry
                            {
                                Code = item["code"].AsString(),
                                Quantity = item["quantity"].AsInt(1),
                                Id = item["id"].AsString(upgrades != null ? item["code"].AsString() : null),
                                Upgrades = upgrades
                            });
                        }
                    }
                    result.Add(set);
                }
            }
            else if (attributes["loadout"].Exists)
            {
                var set = new LoadoutSet { Name = "Default" };
                foreach (var item in attributes["loadout"].AsArray())
                {
                    set.Items.Add(new LoadoutEntry
                    {
                        Code = item["code"].AsString(),
                        Quantity = item["quantity"].AsInt(1)
                    });
                }
                result.Add(set);
            }

            return result;
        }

        // Managed sets from LoadoutManager if present, else the inline JSON sets from attributes.
        public static List<LoadoutSet> GetEffectiveSets(RunningEvent runningEvent, List<LoadoutSet> fallback)
        {
            return GetEffectiveSets(runningEvent.Event.Code, runningEvent.LoadoutManager, fallback);
        }

        public static List<LoadoutSet> GetEffectiveSets(string eventCode, LoadoutManager lm, List<LoadoutSet> fallback)
        {
            if (lm != null && eventCode != null)
            {
                var managed = lm.GetSets(eventCode);
                if (managed != null && managed.Count > 0)
                {
                    return managed.Select(s => new LoadoutSet
                    {
                        Name = s.Name,
                        Items = s.Items.Select(i => new LoadoutEntry
                        {
                            Code = i.Code,
                            Quantity = i.Quantity,
                            Id = i.Id,
                            Upgrades = i.Upgrades
                        }).ToList()
                    }).ToList();
                }
            }
            return fallback;
        }

        // The kits a match of this event hands out, with no match running.
        public static List<LoadoutSet> KitsOf(string eventCode, List<events.CANEventBehavior> behaviors, LoadoutManager lm)
        {
            var provider = behaviors?.OfType<ILoadoutProvider>().FirstOrDefault();
            var inline = provider?.GetInlineLoadoutSets()
                .Select(t => new LoadoutSet { Name = t.name, Items = t.items.ToList() }).ToList();
            return GetEffectiveSets(eventCode, lm, inline ?? new List<LoadoutSet>()) ?? new List<LoadoutSet>();
        }

        public static string[] GetSetNames(List<LoadoutSet> sets)
            => sets.Select(s => s.Name).ToArray();

        public static string[] GetSetItemCodes(List<LoadoutSet> sets)
            => sets.Select(s => string.Join(";", s.Items.Select(i => i.Code + ":" + i.Quantity))).ToArray();

        public static string[] GetSetDescriptions(List<LoadoutSet> sets)
            => sets.Select(s =>
            {
                var itemNames = s.Items.Select(i =>
                {
                    var code = i.Code ?? "";
                    int colonIdx = code.IndexOf(':');
                    var shortName = colonIdx >= 0 ? code.Substring(colonIdx + 1) : code;
                    return i.Quantity > 1 ? shortName + " x" + i.Quantity : shortName;
                });
                return string.Join(", ", itemNames);
            }).ToArray();

        // adjust gets the picked set (empty if none) and returns what to actually equip; must not mutate it.
        public static void Give(IServerPlayer player, RunningEvent runningEvent, List<LoadoutSet> inlineFallback,
            System.Func<LoadoutSet, LoadoutSet> adjust = null)
        {
            var ev = runningEvent.Event;
            var sets = GetEffectiveSets(runningEvent, inlineFallback);

            LoadoutSet chosenSet = sets.FirstOrDefault();
            if (runningEvent.PlayerLoadoutChoices.TryGetValue(player.PlayerUID, out var chosenName))
            {
                var found = sets.FirstOrDefault(s => s.Name == chosenName);
                if (found != null) chosenSet = found;
            }

            // Forced set beats a stale choice, except in modes that assign sets themselves.
            bool modeAssigns = runningEvent.Behaviors
                .OfType<ILoadoutProvider>().FirstOrDefault()?.OffersPlayerChoice == false;
            string forcedName = modeAssigns ? null : ev?.MutatorValue(events.EventMutators.Loadout);
            if (forcedName != null)
            {
                var forced = sets.FirstOrDefault(s
                    => string.Equals(s.Name, forcedName, StringComparison.OrdinalIgnoreCase));
                if (forced != null) chosenSet = forced;
                else runningEvent.sapi.World.Logger.Warning(
                    "Mutator loadout:{0} names no set of event {1}; falling back to the normal pick.",
                    forcedName, ev.Code);
            }

            if (adjust != null) chosenSet = adjust(chosenSet ?? new LoadoutSet { Name = "Default" });
            if (chosenSet == null) return;

            GiveExtra(player, runningEvent, chosenSet);
        }

        // Kit placement rules (worn slots, off-hand, main hand) and mutator filters, for items outside the kit.
        public static void GiveExtra(IServerPlayer player, RunningEvent runningEvent, LoadoutSet set)
        {
            var ev = runningEvent.Event;
            EquipSet(player, runningEvent.sapi, set,
                skipWearables: ev?.HasMutator(events.EventMutators.NoArmor) ?? false,
                skipShields: ev?.HasMutator(events.EventMutators.NoShields) ?? false);
        }

        private static void EquipSet(IServerPlayer player, ICoreServerAPI sapi, LoadoutSet chosenSet,
            bool skipWearables = false, bool skipShields = false)
        {
            var characterInv = player.InventoryManager.GetOwnInventory(GlobalConstants.characterInvClassName);
            var hotbarInv = player.InventoryManager.GetOwnInventory(GlobalConstants.hotBarInvClassName);
            bool mainHandFilled = false;

            // MarkDirty deferred and done parents-first: a sheath synced before its belt hangs the CombatOverhaul client.
            var dressed = new List<ItemSlot>();

            // Retried after the first sync: dependent slots (e.g. sheath on belt) only exist once the parent is synced.
            var unplaced = new List<(ItemStack stack, EnumCharacterDressType? dressType)>();

            foreach (var entry in chosenSet.Items)
            {
                var stack = ItemLookup.StackOf(sapi.World, entry.Code, entry.Quantity);
                if (stack == null)
                {
                    sapi.World.Logger.Warning("Loadout item not found: {0}", entry.Code);
                    continue;
                }

                IssuedGear.Mark(stack, player.PlayerName);
                if (entry.DropOnDeath) IssuedGear.MarkDropOnDeath(stack);
                if (entry.ShopItem != null) MatchShopEB.MarkBought(stack, entry.ShopItem);

                // Filtered items are skipped entirely, not put in the backpack.
                if (skipShields && stack.Collectible is Item shieldCheck
                    && shieldCheck.Code.Path.StartsWith("shield")) continue;

                // Via CanHold, not by dress type index: CO disables some slots and indexing into them crashes its damage handler.
                string clothesCat = stack.Collectible.Attributes?["clothescategory"]?.AsString(null)
                    ?? stack.Collectible.Attributes?["clothesCategoryByType"]?.AsObject<Dictionary<string, string>>()?.Values.FirstOrDefault();
                if (clothesCat != null && skipWearables) continue;
                if (clothesCat != null && characterInv != null)
                {
                    EnumCharacterDressType? dressType =
                        Enum.TryParse<EnumCharacterDressType>(clothesCat, true, out var parsed)
                            ? parsed
                            : (EnumCharacterDressType?)null;

                    var worn = Compat.CharacterInventoryCompat.Wear(characterInv, stack, dressType);
                    if (worn != null) dressed.Add(worn);
                    else unplaced.Add((stack, dressType));
                    continue;
                }

                // hotbar slot 11 = off-hand
                if (hotbarInv != null && stack.Collectible is Item itemObj
                    && itemObj.Code.Path.StartsWith("shield"))
                {
                    var offhandSlot = hotbarInv[11];
                    if (offhandSlot.Itemstack == null)
                    {
                        offhandSlot.Itemstack = stack;
                        offhandSlot.MarkDirty();
                        continue;
                    }
                }

                // first tool goes to hotbar slot 0
                if (!mainHandFilled && hotbarInv != null && stack.Collectible.Tool != null)
                {
                    var mainSlot = hotbarInv[0];
                    if (mainSlot.Itemstack == null)
                    {
                        mainSlot.Itemstack = stack;
                        mainSlot.MarkDirty();
                        mainHandFilled = true;
                        continue;
                    }
                }

                InventoryUtil.Give(sapi, player, stack, "loadout");
            }

            // Diagnostic for the CO client hang on teleport-in.
            LogGearOrderDiagnostics(sapi, player, chosenSet.Name, dressed);

            // A dependent without its parent is pulled out and given normally instead of synced.
            Action<ItemStack> rescue = stack =>
            {
                sapi.World.Logger.Warning(
                    "Loadout for {0}: no wearable slot took '{1}'; given normally instead.",
                    player.PlayerName, stack.Collectible?.Code?.ToString() ?? "<null>");
                InventoryUtil.Give(sapi, player, stack, "loadout");
            };
            Compat.GearSlotOrderCompat.MarkDirtyAfterFill(dressed, rescue);

            // Second try now that dependent slots may exist.
            if (unplaced.Count > 0)
            {
                var lateDressed = new List<ItemSlot>();
                foreach (var (stack, dressType) in unplaced)
                {
                    var worn = Compat.CharacterInventoryCompat.Wear(characterInv, stack, dressType);
                    if (worn != null) lateDressed.Add(worn);
                    else rescue(stack);
                }
                Compat.GearSlotOrderCompat.MarkDirtyAfterFill(lateDressed, rescue);
            }
        }

        // Logs fill order (JSON order), which is what 1.1.7 synced as-is.
        private static void LogGearOrderDiagnostics(ICoreServerAPI sapi, IServerPlayer player,
            string setName, List<ItemSlot> dressed)
        {
            var parts = new List<string>();
            var enablersSeen = new HashSet<string>();
            bool dependentBeforeParent = false;
            bool anyHierarchy = false;

            foreach (var slot in dressed)
            {
                var stack = slot?.Itemstack;
                if (stack == null) continue;
                string role = Compat.GearSlotOrderCompat.RoleOf(stack);
                string code = stack.Collectible?.Code?.ToString() ?? "<null>";
                if (role == null) continue;

                anyHierarchy = true;
                parts.Add(code + " (" + role + ")");
                int colon = role.IndexOf(':');
                string kind = role.Substring(colon + 1);
                if (role.StartsWith("enabler")) enablersSeen.Add(kind);
                else if (!enablersSeen.Contains(kind)) dependentBeforeParent = true;
            }

            if (!anyHierarchy) return;

            string summary = string.Format(
                "Loadout gear-order diag: set '{0}' for {1}, seating order: {2}",
                setName, player.PlayerName, string.Join(", ", parts));

            if (dependentBeforeParent)
                sapi.World.Logger.Warning(
                    "{0} - dependent seated BEFORE its parent; 1.1.7 announced this order verbatim, "
                    + "which hangs a CombatOverhaul client silently. 1.1.8 reorders it (parents first).",
                    summary);
            else
                sapi.World.Logger.Notification("{0}", summary);
        }
    }
}
