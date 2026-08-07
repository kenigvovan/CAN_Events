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
        // Runtime-only: true while a match is live here. Must NOT persist - a mid-match SavePlace would
        // write Occupied=true, and nothing resets it on load, so the place would be unusable forever.
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

        // Per-behavior world data (positions, settings specific to a behavior and this place).
        // Key = behavior name (e.g. "Spleef"), value = arbitrary JSON object.
        public Dictionary<string, JObject> BehaviorData { get; set; } = new Dictionary<string, JObject>();

        public EventPlace(long Id, string Name, string Description, string[] placeTypes, List<BlockPos> spawnPositions)
        {
            this.Id = Id;
            this.Name = Name;
            this.Description = Description;
            this.placeTypes = placeTypes;
            this.spawnPositions = spawnPositions;
        }
        public EventPlace()
        {
            spawnPositions = new List<BlockPos> { };
        }

        /// <summary>
        /// Whether this place can host an event needing every one of these tags right now. The single
        /// definition of "usable arena", shared by the scheduler (announce) and the lookup (pick) -
        /// a looser test in one announces events the other then fails to place.
        /// </summary>
        public bool IsAvailableFor(string[] searchTypes)
        {
            return placeTypes != null
                && searchTypes != null
                && searchTypes.All(t => placeTypes.Contains(t))
                && !Occupied
                && Enabled
                && spawnPositions != null
                && spawnPositions.Count > 0;
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

        /// <summary>
        /// Sets the footprint from two marked corners, in any order. Callers mark corners by walking to
        /// them, so sorting here keeps all three entry points from having their own idea of "min".
        /// </summary>
        public void SetBounds(BlockPos a, BlockPos b)
        {
            HasBounds = true;
            BoundsMin = new BlockPos(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z), a.dimension);
            BoundsMax = new BlockPos(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z), a.dimension);
        }

        /// <summary>
        /// Replaces the spawns with the default bounded-arena layout: two opposite edges, one block
        /// above the platform, level with its middle (spleef-style). No-op without bounds.
        /// </summary>
        public void GenerateEdgeSpawns()
        {
            if (!HasBounds || BoundsMin == null || BoundsMax == null) return;

            spawnPositions ??= new List<BlockPos>();
            spawnPositions.Clear();
            int spawnY = BoundsMax.Y + 1;
            int centerZ = (BoundsMin.Z + BoundsMax.Z) / 2;
            int dim = BoundsMin.dimension;
            spawnPositions.Add(new BlockPos(BoundsMin.X + 1, spawnY, centerZ, dim));
            spawnPositions.Add(new BlockPos(BoundsMax.X - 1, spawnY, centerZ, dim));
        }

        /// <summary>Read this place's per-behavior data blob for the given key (see BehaviorKeys).</summary>
        public bool TryGetBehaviorData(string key, out JObject data)
        {
            data = null;
            return BehaviorData != null && BehaviorData.TryGetValue(key, out data) && data != null;
        }

        /// <summary>Get the per-behavior data blob for the given key, creating an empty one if absent.</summary>
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
