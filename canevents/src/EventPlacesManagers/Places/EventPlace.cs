using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.MathTools;

namespace canevents.src.EventPlacesManagers.Places
{
    public class EventPlace
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        [JsonConverter(typeof(PlaceTagsConverter))]
        public string[] placeTypes { get; set; }
        // Runtime only; nothing resets it on load, so a persisted true would lock the place forever.
        [JsonIgnore]
        public bool Occupied { get; set; }
        public bool Enabled { get; set; } = true;
        public List<BlockPos> spawnPositions { get; set; }
        public bool HasBounds { get; set; }
        public BlockPos BoundsMin { get; set; }
        public BlockPos BoundsMax { get; set; }

        // If set, schematic is placed at event start and restored at event end.
        public bool SchematicOnDemand { get; set; }
        public string SchematicTemplateCode { get; set; }
        public BlockPos SchematicOrigin { get; set; }

        // Keyed by BehaviorKeys.
        public Dictionary<string, JObject> BehaviorData { get; set; } = new Dictionary<string, JObject>();

        public EventPlace()
        {
            spawnPositions = new List<BlockPos> { };
        }

        public bool IsAvailableFor(string[] searchTypes)
            => PlaceAvailability.Diagnose(this, searchTypes) == PlaceProblem.None;

        public bool HasAllTags(IEnumerable<string> tags)
        {
            if (placeTypes == null || tags == null) return false;
            foreach (var t in tags)
                if (!placeTypes.Contains(t)) return false;
            return true;
        }
        public void AddType(string newType)
        {
            newType = PlaceTags.Normalize(newType);
            if (string.IsNullOrEmpty(newType)) return;
            if (this.placeTypes == null)
            {
                this.placeTypes = new string[] { newType };
            }
            else if (!this.placeTypes.Contains(newType))
            {
                this.placeTypes = this.placeTypes.Append(newType).ToArray();
            }
        }

        public bool RemoveType(string type)
        {
            type = PlaceTags.Normalize(type);
            if (string.IsNullOrEmpty(type) || placeTypes == null || !placeTypes.Contains(type)) return false;
            placeTypes = placeTypes.Where(t => t != type).ToArray();
            return true;
        }

        public void SetBounds(BlockPos a, BlockPos b)
        {
            HasBounds = true;
            BoundsMin = new BlockPos(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z), a.dimension);
            BoundsMax = new BlockPos(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z), a.dimension);
        }

        // Two spawns on opposite X edges. Falls back to the lower corner's Y if no floor is found.
        public void GenerateEdgeSpawns(Func<int, int, int?> highestSolidY = null)
        {
            if (!HasBounds || BoundsMin == null || BoundsMax == null) return;

            spawnPositions ??= new List<BlockPos>();
            spawnPositions.Clear();
            int centerZ = (BoundsMin.Z + BoundsMax.Z) / 2;
            int dim = BoundsMin.dimension;
            foreach (int x in new[] { BoundsMin.X + 1, BoundsMax.X - 1 })
            {
                int floorY = highestSolidY?.Invoke(x, centerZ) ?? BoundsMin.Y;
                spawnPositions.Add(new BlockPos(x, floorY + 1, centerZ, dim));
            }
        }

        public bool TryGetBehaviorData(string key, out JObject data)
        {
            data = null;
            return BehaviorData != null && BehaviorData.TryGetValue(key, out data) && data != null;
        }

        public JObject GetOrCreateBehaviorData(string key)
        {
            BehaviorData ??= new Dictionary<string, JObject>();
            if (!BehaviorData.TryGetValue(key, out var data) || data == null)
            {
                data = new JObject();
                BehaviorData[key] = data;
            }
            return data;
        }
    }
}
