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

            if (attributes["loadoutSets"].Exists)
            {
                foreach (var setObj in attributes["loadoutSets"].AsArray())
                {
                    var set = new LoadoutSet { Name = setObj["name"].AsString("Default") };
                    if (setObj["items"].Exists)
                    {
                        foreach (var item in setObj["items"].AsArray())
                        {
                            set.Items.Add(new LoadoutEntry
                            {
                                Code = item["code"].AsString(),
                                Quantity = item["quantity"].AsInt(1)
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
                        Items = s.Items.Select(i => new LoadoutEntry { Code = i.Code, Quantity = i.Quantity }).ToList()
                    }).ToList();
                }
            }
            return fallback;
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

        // Picks the player's chosen set (or first available) and equips it.
        public static void Give(IServerPlayer player, RunningEvent runningEvent, List<LoadoutSet> inlineFallback)
        {
            var sets = GetEffectiveSets(runningEvent, inlineFallback);
            LoadoutSet chosenSet = sets.FirstOrDefault();
            if (runningEvent.PlayerLoadoutChoices.TryGetValue(player.PlayerUID, out var chosenName))
            {
                var found = sets.FirstOrDefault(s => s.Name == chosenName);
                if (found != null) chosenSet = found;
            }
            if (chosenSet == null) return;

            EquipSet(player, runningEvent.sapi, chosenSet);
        }

        private static void EquipSet(IServerPlayer player, ICoreServerAPI sapi, LoadoutSet chosenSet)
        {
            var characterInv = player.InventoryManager.GetOwnInventory(GlobalConstants.characterInvClassName);
            var hotbarInv = player.InventoryManager.GetOwnInventory(GlobalConstants.hotBarInvClassName);
            bool mainHandFilled = false;

            foreach (var entry in chosenSet.Items)
            {
                ItemStack stack = null;

                var item = sapi.World.GetItem(new AssetLocation(entry.Code));
                if (item != null)
                {
                    stack = new ItemStack(item, entry.Quantity);
                }
                else
                {
                    var block = sapi.World.GetBlock(new AssetLocation(entry.Code));
                    if (block != null)
                    {
                        stack = new ItemStack(block, entry.Quantity);
                    }
                }

                if (stack == null)
                {
                    sapi.World.Logger.Warning("Loadout item not found: {0}", entry.Code);
                    continue;
                }

                stack.Attributes.SetBool("canevents_loadout", true);
                stack.Attributes.SetString("canevents_issuedTo", player.PlayerName);

                // Wearable armor → character inventory. Vanilla routes by EnumCharacterDressType slot
                // index; Combat Overhaul replaces it with ArmorInventory, so fall back to slot.CanHold.
                string clothesCat = stack.Collectible.Attributes?["clothescategory"]?.AsString(null)
                    ?? stack.Collectible.Attributes?["clothesCategoryByType"]?.AsObject<Dictionary<string, string>>()?.Values.FirstOrDefault();
                if (clothesCat != null && characterInv != null)
                {
                    bool combatOverhaulActive = sapi.ModLoader.IsModEnabled("combatoverhaul");
                    bool placed = false;

                    if (!combatOverhaulActive && Enum.TryParse<EnumCharacterDressType>(clothesCat, true, out var dressType)
                        && (int)dressType >= 0 && (int)dressType < characterInv.Count)
                    {
                        // Vanilla VS has one slot per clothes category, so multi-layer loadouts
                        // collide; last item wins, putting the outer JSON layer on.
                        var slot = characterInv[(int)dressType];
                        slot.Itemstack = stack;
                        slot.MarkDirty();
                        placed = true;
                    }

                    if (!placed)
                    {
                        var dummy = new DummySlot(stack);
                        // First pass: prefer an empty matching slot.
                        for (int i = 0; i < characterInv.Count; i++)
                        {
                            var slot = characterInv[i];
                            if (slot.Itemstack != null) continue;
                            if (!slot.CanHold(dummy)) continue;
                            slot.Itemstack = stack;
                            slot.MarkDirty();
                            placed = true;
                            break;
                        }
                        // Second pass: overwrite the last matching slot, so layered loadouts
                        // collapse to "last layer in the JSON wins".
                        if (!placed)
                        {
                            for (int i = characterInv.Count - 1; i >= 0; i--)
                            {
                                var slot = characterInv[i];
                                if (!slot.CanHold(dummy)) continue;
                                slot.Itemstack = stack;
                                slot.MarkDirty();
                                placed = true;
                                break;
                            }
                        }
                    }

                    if (placed) continue;
                    sapi.World.Logger.Warning("Could not equip armor {0} on {1}", entry.Code, player.PlayerName);
                    continue;
                }

                // Shield → off-hand (hotbar slot 11)
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

                // First weapon/tool → main hand (hotbar slot 0)
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
        }
    }
}
