using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace canevents.src.EventPlacesManagers
{
    public class ArenaTemplate
    {
        public string Code;
        public string SchematicPath;
        public string[] PlaceTypes;
        public SpawnOffset[] SpawnOffsets;
        public string Description;
        public bool OnDemand; // if true, blocks are placed only while event runs

        // Everything below is relative to the stamp origin and optional: a template without it gets
        // the schematic footprint as bounds and no mode data (see TemplateLayout).

        // Bounds corners; null = the whole schematic footprint (except Spleef, see TemplateLayout).
        public SpawnOffset BoundsMin;
        public SpawnOffset BoundsMax;

        // Per-mode place data (hill, posts, checkpoints, loot, cage...), positions as offsets from
        // the origin, so each stamp comes fully set up.
        public Dictionary<string, JObject> BehaviorData;
    }

    public class SpawnOffset
    {
        public int X, Y, Z;
    }
}
