using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace canevents.src.EventPlacesManagers
{
    public class PlacedSchematicData
    {
        public long PlaceId;
        public Dictionary<string, int> SavedBlocks = new Dictionary<string, int>();
    }

    public class SchematicPlacer
    {
        private ICoreServerAPI sapi;
        private string savedBlocksFolder;

        public SchematicPlacer(ICoreServerAPI sapi, string savedBlocksFolder)
        {
            this.sapi = sapi;
            this.savedBlocksFolder = savedBlocksFolder;

            if (!Directory.Exists(savedBlocksFolder))
            {
                Directory.CreateDirectory(savedBlocksFolder);
            }
        }

        /// <summary>
        /// Reads a schematic's dimensions without placing blocks. (0,0,0) if it can't be loaded.
        /// </summary>
        public Vec3i GetSchematicSize(ArenaTemplate template)
        {
            string json = null;
            var userPath = Path.Combine(GamePaths.ModConfig, "canevents", "schematics", template.SchematicPath + ".json");
            if (File.Exists(userPath))
            {
                try { json = File.ReadAllText(userPath); }
                catch { return new Vec3i(0, 0, 0); }
            }
            else
            {
                var asset = sapi.Assets.TryGet(new AssetLocation("canevents", template.SchematicPath + ".json"));
                if (asset == null) return new Vec3i(0, 0, 0);
                json = asset.ToText();
            }
            try
            {
                var schematic = JsonConvert.DeserializeObject<BlockSchematic>(json);
                if (schematic == null) return new Vec3i(0, 0, 0);
                return new Vec3i(schematic.SizeX, schematic.SizeY, schematic.SizeZ);
            }
            catch { return new Vec3i(0, 0, 0); }
        }

        public bool Place(ArenaTemplate template, BlockPos origin, long placeId)
        {
            string json = null;

            // Prefer user-editable schematic in ModConfig/canevents/schematics/
            var userPath = Path.Combine(GamePaths.ModConfig, "canevents", "schematics", template.SchematicPath + ".json");
            if (File.Exists(userPath))
            {
                try
                {
                    json = File.ReadAllText(userPath);
                }
                catch (Exception ex)
                {
                    sapi.World.Logger.Error("SchematicPlacer: Failed to read user schematic {0}: {1}", userPath, ex.Message);
                    return false;
                }
            }
            else
            {
                var asset = sapi.Assets.TryGet(new AssetLocation("canevents", template.SchematicPath + ".json"));
                if (asset == null)
                {
                    sapi.World.Logger.Error("SchematicPlacer: Schematic not found: {0} (checked {1} and asset canevents:{0}.json)", template.SchematicPath, userPath);
                    return false;
                }
                json = asset.ToText();
            }

            BlockSchematic schematic;

            try
            {
                schematic = JsonConvert.DeserializeObject<BlockSchematic>(json);
                if (schematic == null)
                {
                    sapi.World.Logger.Error("SchematicPlacer: Schematic deserialized as null: {0}", template.SchematicPath);
                    return false;
                }
            }
            catch (Exception ex)
            {
                sapi.World.Logger.Error("SchematicPlacer: Failed to parse schematic {0}: {1}", template.SchematicPath, ex.Message);
                return false;
            }

            schematic.Init(sapi.World.BlockAccessor);

            // Save existing blocks before placing
            var savedBlocks = new Dictionary<string, int>();
            var blockAccessor = sapi.World.BlockAccessor;

            for (int x = 0; x < schematic.SizeX; x++)
            {
                for (int y = 0; y < schematic.SizeY; y++)
                {
                    for (int z = 0; z < schematic.SizeZ; z++)
                    {
                        var pos = new BlockPos(origin.X + x, origin.Y + y, origin.Z + z, origin.dimension);
                        savedBlocks[pos.X + "," + pos.Y + "," + pos.Z + "," + pos.dimension] = blockAccessor.GetBlock(pos).BlockId;
                    }
                }
            }

            // Write the rollback file BEFORE touching the world: a crash in between would otherwise
            // leave the schematic standing with no record of the terrain under it. The reverse window
            // is harmless (a file with nothing placed restores the blocks already there).
            var data = new PlacedSchematicData
            {
                PlaceId = placeId,
                SavedBlocks = savedBlocks
            };
            if (!SavePlacedData(data))
            {
                sapi.World.Logger.Error(
                    "SchematicPlacer: could not write rollback data for place {0}; not placing '{1}'.",
                    placeId, template.Code);
                return false;
            }

            schematic.Place(blockAccessor, sapi.World, origin);
            blockAccessor.Commit();

            sapi.World.Logger.Notification("SchematicPlacer: Placed '{0}' at {1},{2},{3}",
                template.Code, origin.X, origin.Y, origin.Z);
            return true;
        }

        public bool Restore(long placeId)
        {
            var data = LoadPlacedData(placeId);
            if (data == null) return false;

            var blockAccessor = sapi.World.BlockAccessor;

            // Skip unparseable keys instead of throwing: one corrupt entry aborting the restore leaves
            // the file behind and repeats the failure on every server start.
            int skipped = 0;
            foreach (var kvp in data.SavedBlocks)
            {
                var parts = kvp.Key.Split(',');
                if (parts.Length < 3
                    || !int.TryParse(parts[0], out int x)
                    || !int.TryParse(parts[1], out int y)
                    || !int.TryParse(parts[2], out int z))
                {
                    skipped++;
                    continue;
                }

                int dim = 0;
                if (parts.Length >= 4 && !int.TryParse(parts[3], out dim)) dim = 0;

                blockAccessor.SetBlock(kvp.Value, new BlockPos(x, y, z, dim));
            }

            if (skipped > 0)
                sapi.World.Logger.Warning(
                    "SchematicPlacer: restore for place {0} skipped {1} unreadable block key(s).",
                    placeId, skipped);

            blockAccessor.Commit();
            DeletePlacedData(placeId);

            sapi.World.Logger.Notification("SchematicPlacer: Restored blocks for place ID {0}", placeId);
            return true;
        }

        public bool HasSavedData(long placeId)
        {
            return File.Exists(GetDataPath(placeId));
        }

        /// <summary>
        /// Restores every schematic arena left placed by an unclean shutdown. Loads the affected chunks
        /// first (usually unloaded at startup), then restores via the normal path. Best-effort; call once
        /// the world is running. <paramref name="keepPlaceIds"/> are persistent arenas that keep their
        /// rollback file on purpose - restoring them would wipe a live arena on every normal restart.
        /// </summary>
        public void RestoreAllOrphaned(ISet<long> keepPlaceIds = null)
        {
            string[] files;
            try { files = Directory.GetFiles(savedBlocksFolder, "placed_*.json"); }
            catch (Exception ex)
            {
                sapi.World.Logger.Warning("SchematicPlacer: orphan sweep skipped: {0}", ex.Message);
                return;
            }
            if (files.Length == 0) return;

            foreach (var file in files)
            {
                PlacedSchematicData data;
                try { data = JsonConvert.DeserializeObject<PlacedSchematicData>(File.ReadAllText(file)); }
                catch { data = null; }

                if (data == null || data.SavedBlocks == null || data.SavedBlocks.Count == 0
                    || !TryGetChunkBounds(data.SavedBlocks, out int cx1, out int cz1, out int cx2, out int cz2))
                {
                    try { File.Delete(file); } catch { } // corrupt/empty: drop so it can't linger
                    continue;
                }

                long id = data.PlaceId;

                // Persistent placed arena: keep its blocks and its rollback file.
                if (keepPlaceIds != null && keepPlaceIds.Contains(id)) continue;

                // LoadChunkColumnPriority only reaches dimension 0; restoring elsewhere would write
                // nothing while Restore deletes the rollback file, losing the terrain. Keep the file.
                int dim = DimensionOf(data.SavedBlocks);
                if (dim != 0)
                {
                    sapi.World.Logger.Warning(
                        "SchematicPlacer: place {0} was left placed in dimension {1}; the startup sweep only "
                        + "loads dimension 0, so its rollback file is kept for a manual restore.", id, dim);
                    continue;
                }

                // 1-chunk margin guards boundary / negative-coord truncation.
                sapi.WorldManager.LoadChunkColumnPriority(cx1 - 1, cz1 - 1, cx2 + 1, cz2 + 1, new ChunkLoadOptions
                {
                    OnLoaded = () => Restore(id)
                });
            }
        }

        /// <summary>
        /// Dimension of the saved blocks, from the "x,y,z,dim" keys. Legacy three-part keys mean
        /// dimension 0. A placement never spans dimensions, so the first readable key decides.
        /// </summary>
        private static int DimensionOf(Dictionary<string, int> savedBlocks)
        {
            foreach (var key in savedBlocks.Keys)
            {
                var parts = key.Split(',');
                if (parts.Length < 4) return 0;
                if (int.TryParse(parts[3], out int dim)) return dim;
            }
            return 0;
        }

        // Min/max chunk (X,Z) covering the saved block positions ("x,y,z,dim" keys). 32-block chunks.
        private static bool TryGetChunkBounds(Dictionary<string, int> savedBlocks,
            out int cx1, out int cz1, out int cx2, out int cz2)
        {
            cx1 = cz1 = int.MaxValue;
            cx2 = cz2 = int.MinValue;
            bool any = false;
            foreach (var key in savedBlocks.Keys)
            {
                var parts = key.Split(',');
                if (parts.Length < 3) continue;
                if (!int.TryParse(parts[0], out int x) || !int.TryParse(parts[2], out int z)) continue;
                // Floor division: plain x/32 truncates toward zero (x=-5 → chunk 0, not -1), which would
                // load the wrong columns for a negative-coordinate arena.
                int cx = (int)Math.Floor(x / 32.0), cz = (int)Math.Floor(z / 32.0);
                if (cx < cx1) cx1 = cx;
                if (cx > cx2) cx2 = cx;
                if (cz < cz1) cz1 = cz;
                if (cz > cz2) cz2 = cz;
                any = true;
            }
            return any;
        }

        // Atomic: this file is the only record of the arena's original terrain, so a truncated
        // one loses it. Returns false when the write failed; the caller must then not place.
        private bool SavePlacedData(PlacedSchematicData data)
        {
            if (Scoring.JsonStore.TryWriteAtomic(GetDataPath(data.PlaceId), data, out var error)) return true;

            sapi?.World?.Logger?.Error("SchematicPlacer: failed to save rollback data for place {0}: {1}",
                data.PlaceId, error);
            return false;
        }

        private PlacedSchematicData LoadPlacedData(long placeId)
        {
            string path = GetDataPath(placeId);
            if (!File.Exists(path)) return null;

            try
            {
                return JsonConvert.DeserializeObject<PlacedSchematicData>(File.ReadAllText(path));
            }
            catch
            {
                return null;
            }
        }

        private void DeletePlacedData(long placeId)
        {
            string path = GetDataPath(placeId);
            if (File.Exists(path)) File.Delete(path);
        }

        private string GetDataPath(long placeId)
        {
            return Path.Combine(savedBlocksFolder, "placed_" + placeId + ".json");
        }
    }
}
