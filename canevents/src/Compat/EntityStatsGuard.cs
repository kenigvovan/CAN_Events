using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;

namespace canevents.src.Compat
{
    // Snapshots Entity.Stats before a match and restores after. Inventory-derived modifiers
    // (wearablemod, carryon:Hands, XSkills ability-nudist/armorexpert) get stuck mid-match otherwise.
    // Not used for own-gear matches.
    public static class EntityStatsGuard
    {
        // Layout: category -> modifier code -> {v, w, p}.
        private const string ValueKey = "v";
        private const string WeightKey = "w";
        private const string PersistentKey = "p";

        public static ITreeAttribute Snapshot(Entity entity)
        {
            if (entity?.Stats == null) return null;

            var tree = new TreeAttribute();
            foreach (var category in entity.Stats)
            {
                var categoryTree = new TreeAttribute();
                foreach (var modifier in category.Value.ValuesByKey)
                {
                    var entry = new TreeAttribute();
                    entry.SetFloat(ValueKey, modifier.Value.Value);
                    entry.SetFloat(WeightKey, modifier.Value.Weight);
                    entry.SetBool(PersistentKey, modifier.Value.Persistent);
                    categoryTree[modifier.Key] = entry;
                }
                tree[category.Key] = categoryTree;
            }
            return tree.Count > 0 ? tree : null;
        }

        // Returns modifiers changed; 0 is normal. Categories not in the snapshot are left alone.
        public static int Restore(Entity entity, ITreeAttribute snapshot)
        {
            if (entity?.Stats == null || snapshot == null) return 0;

            int changed = 0;
            foreach (var category in snapshot)
            {
                string categoryName = category.Key;
                if (!(category.Value is ITreeAttribute savedModifiers)) continue;

                // Copy codes first, Set/Remove break an open enumerator.
                foreach (var code in LiveModifiers(entity, categoryName))
                {
                    if (savedModifiers.HasAttribute(code)) continue;
                    entity.Stats.Remove(categoryName, code);
                    changed++;
                }

                foreach (var saved in savedModifiers)
                {
                    if (!(saved.Value is ITreeAttribute entry)) continue;

                    float value = entry.GetFloat(ValueKey);
                    bool persistent = entry.GetBool(PersistentKey);
                    if (Matches(entity, categoryName, saved.Key, value, persistent)) continue;

                    entity.Stats.Set(categoryName, saved.Key, value, persistent);
                    changed++;
                }
            }

            // EntityStats has no weight setter.
            RestoreWeights(entity, snapshot);

            // EntityPlayer caches blended walkspeed separately.
            if (entity is EntityPlayer entityPlayer)
                entityPlayer.walkSpeed = entity.Stats.GetBlended("walkspeed");
            return changed;
        }

        private static List<string> LiveModifiers(Entity entity, string category)
        {
            var codes = new List<string>();
            var stats = Category(entity, category);
            if (stats == null) return codes;

            foreach (var modifier in stats.ValuesByKey) codes.Add(modifier.Key);
            return codes;
        }

        private static bool Matches(Entity entity, string category, string code, float value, bool persistent)
        {
            var stats = Category(entity, category);
            if (stats == null) return false;
            if (!stats.ValuesByKey.TryGetValue(code, out var live)) return false;
            return Math.Abs(live.Value - value) < 0.0001f && live.Persistent == persistent;
        }

        private static void RestoreWeights(Entity entity, ITreeAttribute snapshot)
        {
            foreach (var category in snapshot)
            {
                if (!(category.Value is ITreeAttribute savedModifiers)) continue;
                var stats = Category(entity, category.Key);
                if (stats == null) continue;

                foreach (var saved in savedModifiers)
                {
                    if (!(saved.Value is ITreeAttribute entry)) continue;
                    if (!stats.ValuesByKey.TryGetValue(saved.Key, out var live)) continue;
                    live.Weight = entry.GetFloat(WeightKey, 1f);
                }
            }
        }

        // EntityStats only exposes its table through iteration.
        private static EntityFloatStats Category(Entity entity, string category)
        {
            foreach (var stat in entity.Stats)
                if (stat.Key == category) return stat.Value;
            return null;
        }
    }
}
