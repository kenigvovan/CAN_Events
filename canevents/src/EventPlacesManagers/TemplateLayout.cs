using System.Collections.Generic;
using System.Linq;
using canevents.src.EventPlacesManagers.Places;
using Newtonsoft.Json.Linq;
using Vintagestory.API.MathTools;

namespace canevents.src.EventPlacesManagers
{
    // Spawns, bounds and mode data of a template, converted between origin offsets and world positions.
    public static class TemplateLayout
    {
        // schematicSize is the fallback footprint when the template has no bounds; zero means unknown.
        public static void Apply(ArenaTemplate template, EventPlace place, BlockPos origin, Vec3i schematicSize)
        {
            place.spawnPositions ??= new List<BlockPos>();
            place.spawnPositions.Clear();
            if (template.SpawnOffsets != null && template.SpawnOffsets.Length > 0)
            {
                foreach (var o in template.SpawnOffsets) place.spawnPositions.Add(Offset(origin, o));
            }
            else
            {
                // Fallback so the place is at least reachable.
                place.spawnPositions.Add(origin.Copy());
            }

            if (template.BoundsMin != null && template.BoundsMax != null)
            {
                place.SetBounds(Offset(origin, template.BoundsMin), Offset(origin, template.BoundsMax));
            }
            else if (schematicSize != null && schematicSize.X > 0 && schematicSize.Z > 0
                     && !(place.placeTypes?.Contains(PlaceTags.Spleef) ?? false))
            {
                // Not for spleef: it treats bounds as the platform footprint.
                place.SetBounds(origin.Copy(), new BlockPos(
                    origin.X + schematicSize.X - 1,
                    origin.Y + System.Math.Max(schematicSize.Y, 1) - 1,
                    origin.Z + schematicSize.Z - 1,
                    origin.dimension));
            }

            if (template.BehaviorData != null)
            {
                place.BehaviorData = new Dictionary<string, JObject>();
                foreach (var kv in template.BehaviorData)
                {
                    if (kv.Value == null) continue;
                    var copy = (JObject)kv.Value.DeepClone();
                    Shift(copy, origin.X, origin.Y, origin.Z, origin.dimension);
                    place.BehaviorData[kv.Key] = copy;
                }
            }
        }

        public static void Capture(ArenaTemplate template, EventPlace place, BlockPos origin)
        {
            template.SpawnOffsets = (place.spawnPositions ?? new List<BlockPos>())
                .Select(p => ToOffset(origin, p))
                .ToArray();

            if (PlaceBounds.Has(place))
            {
                template.BoundsMin = ToOffset(origin, place.BoundsMin);
                template.BoundsMax = ToOffset(origin, place.BoundsMax);
            }
            else
            {
                template.BoundsMin = template.BoundsMax = null;
            }

            if (place.BehaviorData == null || place.BehaviorData.Count == 0)
            {
                template.BehaviorData = null;
                return;
            }
            template.BehaviorData = new Dictionary<string, JObject>();
            foreach (var kv in place.BehaviorData)
            {
                if (kv.Value == null) continue;
                var copy = (JObject)kv.Value.DeepClone();
                Shift(copy, -origin.X, -origin.Y, -origin.Z, null);
                template.BehaviorData[kv.Key] = copy;
            }
        }

        public static string Describe(ArenaTemplate template)
        {
            var parts = new List<string> { (template.SpawnOffsets?.Length ?? 0) + " spawns" };
            if (template.BoundsMin != null && template.BoundsMax != null) parts.Add("bounds");
            if (template.BehaviorData != null && template.BehaviorData.Count > 0)
                parts.Add(string.Join(", ", template.BehaviorData.Keys));
            return string.Join(", ", parts);
        }

        private static BlockPos Offset(BlockPos origin, SpawnOffset o)
            => new BlockPos(origin.X + o.X, origin.Y + o.Y, origin.Z + o.Z, origin.dimension);

        private static SpawnOffset ToOffset(BlockPos origin, BlockPos p)
            => new SpawnOffset { X = p.X - origin.X, Y = p.Y - origin.Y, Z = p.Z - origin.Z };

        // Positions found by shape: any {X,Y,Z}, spleef cage/platform fields, parkour FallY.
        // dim null (Capture) strips the dimension, offsets have none.
        private static void Shift(JToken token, int dx, int dy, int dz, int? dim)
        {
            if (token is JArray arr)
            {
                foreach (var item in arr) Shift(item, dx, dy, dz, dim);
                return;
            }
            if (!(token is JObject obj)) return;

            if (obj["X"] != null && obj["Y"] != null && obj["Z"] != null)
            {
                Add(obj, "X", dx);
                Add(obj, "Y", dy);
                Add(obj, "Z", dz);
                if (dim.HasValue) obj["Dim"] = dim.Value;
                else obj.Remove("Dim");
            }

            Add(obj, "CageOriginX", dx);
            Add(obj, "CageOriginY", dy);
            Add(obj, "CageOriginZ", dz);
            if (obj["CageOriginX"] != null)
            {
                if (dim.HasValue) obj["CageOriginDim"] = dim.Value;
                else obj.Remove("CageOriginDim");
            }
            Add(obj, "FallY", dy);
            AddAll(obj, "PlatformX", dx);
            AddAll(obj, "PlatformY", dy);
            AddAll(obj, "PlatformZ", dz);
            if (obj["PlatformDim"] is JArray dims)
            {
                if (dim.HasValue) for (int i = 0; i < dims.Count; i++) dims[i] = dim.Value;
                else obj.Remove("PlatformDim");
            }

            foreach (var prop in obj.Properties().ToList())
            {
                if (prop.Value is JObject || prop.Value is JArray) Shift(prop.Value, dx, dy, dz, dim);
            }
        }

        private static void Add(JObject obj, string key, int delta)
        {
            var v = obj[key];
            if (v == null) return;
            if (v.Type == JTokenType.Integer) obj[key] = v.Value<long>() + delta;
            else if (v.Type == JTokenType.Float) obj[key] = v.Value<double>() + delta;
        }

        private static void AddAll(JObject obj, string key, int delta)
        {
            if (!(obj[key] is JArray arr)) return;
            for (int i = 0; i < arr.Count; i++)
            {
                if (arr[i].Type == JTokenType.Integer) arr[i] = arr[i].Value<long>() + delta;
            }
        }
    }
}
