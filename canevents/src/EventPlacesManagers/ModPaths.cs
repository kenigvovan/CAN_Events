using System.IO;
using Vintagestory.API.Config;

namespace canevents.src
{
    // The mod's folders under ModConfig, spelled once.
    public static class ModPaths
    {
        public static string Root => Path.Combine(GamePaths.ModConfig, "canevents");

        // The authoritative copy; assets only seed it.
        public static string Events => Path.Combine(Root, "events");

        // Preferred over the shipped assets.
        public static string Schematics => Path.Combine(Root, "schematics");

        public static string Arenas => Path.Combine(Root, "arenas");

        // Rollback files: schematic placements and the arena block journal.
        public static string SavedBlocks => Path.Combine(Root, "savedBlocks");

        public static string SchematicFile(string name) => Path.Combine(Schematics, name + ".json");

        public static string ArenaFile(string templateCode) => Path.Combine(Arenas, templateCode + ".json");
    }
}
