using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace canevents.src.EventPlacesManagers
{
    public class PlacedSchematicData
    {
        public long PlaceId;
        public Dictionary<string, int> SavedBlocks = new Dictionary<string, int>();

        // Same keys: the terrain's block entities (chest contents, sign text...) as base64 TreeAttribute
        // bytes, since a block id alone brings a chest back empty (R10). Absent in older files.
        public Dictionary<string, string> SavedEntities;
    }

    public enum SchematicPlaceResult
    {
        Placed,
        Failed,
        // Load was requested; retry shortly.
        ChunksNotLoaded,
        // Previous placement not restored yet.
        AlreadyPlaced
    }

    public class SchematicPlacer
    {
        private ICoreServerAPI sapi;
        private string savedBlocksFolder;

        // Restores waiting on chunk loads, so a second request doesn't schedule twice.
        private readonly HashSet<long> restoreWaiting = new HashSet<long>();
        private const int MaxRestoreAttempts = 6;
        private const int RestoreRetryDelayMs = 5000;

        public SchematicPlacer(ICoreServerAPI sapi, string savedBlocksFolder)
        {
            this.sapi = sapi;
            this.savedBlocksFolder = savedBlocksFolder;

            if (!Directory.Exists(savedBlocksFolder))
            {
                Directory.CreateDirectory(savedBlocksFolder);
            }
        }

        public Vec3i GetSchematicSize(ArenaTemplate template)
            => TryLoadSchematic(template, out var schematic, logErrors: false)
                ? new Vec3i(schematic.SizeX, schematic.SizeY, schematic.SizeZ)
                : new Vec3i(0, 0, 0);

        // User copy in ModConfig wins over the asset.
        public bool TryLoadSchematic(ArenaTemplate template, out BlockSchematic schematic, bool logErrors)
        {
            schematic = null;
            string json;
            var userPath = ModPaths.SchematicFile(template.SchematicPath);
            if (File.Exists(userPath))
            {
                try { json = File.ReadAllText(userPath); }
                catch (Exception ex)
                {
                    if (logErrors) sapi.World.Logger.Error("SchematicPlacer: Failed to read user schematic {0}: {1}", userPath, ex.Message);
                    return false;
                }
            }
            else
            {
                var asset = sapi.Assets.TryGet(new AssetLocation("canevents", template.SchematicPath + ".json"));
                if (asset == null)
                {
                    if (logErrors) sapi.World.Logger.Error("SchematicPlacer: Schematic not found: {0} (checked {1} and asset canevents:{0}.json)", template.SchematicPath, userPath);
                    return false;
                }
                json = asset.ToText();
            }

            try
            {
                schematic = JsonConvert.DeserializeObject<BlockSchematic>(json);
            }
            catch (Exception ex)
            {
                if (logErrors) sapi.World.Logger.Error("SchematicPlacer: Failed to parse schematic {0}: {1}", template.SchematicPath, ex.Message);
                return false;
            }
            if (schematic == null && logErrors)
                sapi.World.Logger.Error("SchematicPlacer: Schematic deserialized as null: {0}", template.SchematicPath);
            return schematic != null;
        }

        public bool Place(ArenaTemplate template, BlockPos origin, long placeId)
            => TryPlace(template, origin, placeId) == SchematicPlaceResult.Placed;

        public SchematicPlaceResult TryPlace(ArenaTemplate template, BlockPos origin, long placeId)
        {
            // Re-recording would save the standing schematic as terrain and lose the original.
            if (HasSavedData(placeId))
            {
                sapi.World.Logger.Warning(
                    "SchematicPlacer: place {0} still has a placement waiting for its restore; not placing '{1}'.",
                    placeId, template.Code);
                return SchematicPlaceResult.AlreadyPlaced;
            }

            if (!TryLoadSchematic(template, out var schematic, logErrors: true)) return SchematicPlaceResult.Failed;

            // Unloaded chunks read as air and ignore SetBlock, so the rollback would record air.
            if (!EnsureLoaded(origin, schematic.SizeX, schematic.SizeY, schematic.SizeZ))
            {
                sapi.World.Logger.Notification(
                    "SchematicPlacer: footprint of '{0}' for place {1} is not fully loaded; load requested.",
                    template.Code, placeId);
                return SchematicPlaceResult.ChunksNotLoaded;
            }

            schematic.Init(sapi.World.BlockAccessor);

            var savedBlocks = new Dictionary<string, int>();
            var savedEntities = new Dictionary<string, string>();
            var blockAccessor = sapi.World.BlockAccessor;

            for (int x = 0; x < schematic.SizeX; x++)
            {
                for (int y = 0; y < schematic.SizeY; y++)
                {
                    for (int z = 0; z < schematic.SizeZ; z++)
                    {
                        var pos = new BlockPos(origin.X + x, origin.Y + y, origin.Z + z, origin.dimension);
                        string key = pos.X + "," + pos.Y + "," + pos.Z + "," + pos.dimension;
                        savedBlocks[key] = blockAccessor.GetBlock(pos).BlockId;
                        string entity = SaveEntity(blockAccessor.GetBlockEntity(pos), pos);
                        if (entity != null) savedEntities[key] = entity;
                    }
                }
            }

            // Rollback file goes to disk before the world is touched, in case of a crash in between.
            var data = new PlacedSchematicData
            {
                PlaceId = placeId,
                SavedBlocks = savedBlocks,
                SavedEntities = savedEntities.Count > 0 ? savedEntities : null
            };
            if (!SavePlacedData(data))
            {
                sapi.World.Logger.Error(
                    "SchematicPlacer: could not write rollback data for place {0}; not placing '{1}'.",
                    placeId, template.Code);
                return SchematicPlaceResult.Failed;
            }

            schematic.Place(blockAccessor, sapi.World, origin);
            blockAccessor.Commit();

            sapi.World.Logger.Notification("SchematicPlacer: Placed '{0}' at {1},{2},{3}",
                template.Code, origin.X, origin.Y, origin.Z);
            return SchematicPlaceResult.Placed;
        }

        // If anything is unloaded, requests the columns in the box's dimension and returns false.
        private bool EnsureLoaded(BlockPos origin, int sizeX, int sizeY, int sizeZ)
        {
            var ba = sapi.World.BlockAccessor;
            int mapHeight = sapi.WorldManager.MapSizeY;
            bool allLoaded = true;
            foreach (int x in ChunkProbes(origin.X, sizeX))
                foreach (int z in ChunkProbes(origin.Z, sizeZ))
                    foreach (int y in ChunkProbes(origin.Y, sizeY))
                    {
                        if (y < 0 || y >= mapHeight) continue;
                        if (ba.GetChunkAtBlockPos(new BlockPos(x, y, z, origin.dimension)) == null) allLoaded = false;
                    }
            if (allLoaded) return true;

            // Fire and forget; the caller's retry re-checks.
            DimensionChunks.LoadArea(sapi,
                DimensionChunks.ColumnOf(origin.X), DimensionChunks.ColumnOf(origin.Z),
                DimensionChunks.ColumnOf(origin.X + Math.Max(sizeX, 1) - 1),
                DimensionChunks.ColumnOf(origin.Z + Math.Max(sizeZ, 1) - 1),
                origin.dimension, onLoaded: null);
            return false;
        }

        private static IEnumerable<int> ChunkProbes(int start, int size)
        {
            int last = start + Math.Max(size, 1) - 1;
            for (int v = start; v < last; v += GlobalConstants.ChunkSize) yield return v;
            yield return last;
        }

        // If any chunk is unloaded, loads it and restores later, keeping the file until then.
        // True when restored or deferred.
        public bool Restore(long placeId)
        {
            if (restoreWaiting.Contains(placeId)) return true;
            return TryRestore(placeId, 0);
        }

        private bool TryRestore(long placeId, int attempt)
        {
            var data = LoadPlacedData(placeId);
            if (data == null || data.SavedBlocks == null)
            {
                restoreWaiting.Remove(placeId);
                return false;
            }

            // Skip bad keys, otherwise one corrupt entry fails the restore on every server start.
            int skipped = 0;
            var blocks = new List<KeyValuePair<BlockPos, int>>(data.SavedBlocks.Count);
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
                blocks.Add(new KeyValuePair<BlockPos, int>(new BlockPos(x, y, z, dim), kvp.Value));
            }

            var blockAccessor = sapi.World.BlockAccessor;
            foreach (var b in blocks)
            {
                if (blockAccessor.GetChunkAtBlockPos(b.Key) != null) continue;
                return DeferRestore(placeId, data, attempt);
            }

            foreach (var b in blocks) blockAccessor.SetBlock(b.Value, b.Key);

            // After every block is back: SetBlock spawned an empty entity, the saved one fills it.
            if (data.SavedEntities != null)
                foreach (var b in blocks)
                    if (data.SavedEntities.TryGetValue(b.Key.X + "," + b.Key.Y + "," + b.Key.Z + "," + b.Key.dimension, out var saved))
                        RestoreEntity(blockAccessor, b.Key, saved);

            if (skipped > 0)
                sapi.World.Logger.Warning(
                    "SchematicPlacer: restore for place {0} skipped {1} unreadable block key(s).",
                    placeId, skipped);

            blockAccessor.Commit();
            DeletePlacedData(placeId);
            restoreWaiting.Remove(placeId);

            sapi.World.Logger.Notification("SchematicPlacer: Restored blocks for place ID {0}", placeId);
            return true;
        }

        // A block entity that won't serialise is logged and left out; the rest of the rollback still goes.
        private string SaveEntity(BlockEntity be, BlockPos pos)
        {
            if (be == null) return null;
            try
            {
                var tree = new TreeAttribute();
                be.ToTreeAttributes(tree);
                return Convert.ToBase64String(tree.ToBytes());
            }
            catch (Exception ex)
            {
                sapi.World.Logger.Warning("SchematicPlacer: could not save the block entity at {0}: {1}", pos, ex.Message);
                return null;
            }
        }

        private void RestoreEntity(IBlockAccessor ba, BlockPos pos, string saved)
        {
            try
            {
                var block = ba.GetBlock(pos);
                if (ba.GetBlockEntity(pos) == null && block?.EntityClass != null) ba.SpawnBlockEntity(block.EntityClass, pos);
                var be = ba.GetBlockEntity(pos);
                if (be == null) return;

                var tree = new TreeAttribute();
                tree.FromBytes(Convert.FromBase64String(saved));
                be.FromTreeAttributes(tree, sapi.World);
                be.MarkDirty(true);
            }
            catch (Exception ex)
            {
                sapi.World.Logger.Warning("SchematicPlacer: could not restore the block entity at {0}: {1}", pos, ex.Message);
            }
        }

        private bool DeferRestore(long placeId, PlacedSchematicData data, int attempt)
        {
            int dim = DimensionOf(data.SavedBlocks);
            if (!TryGetChunkBounds(data.SavedBlocks, out int cx1, out int cz1, out int cx2, out int cz2))
            {
                restoreWaiting.Remove(placeId);
                sapi.World.Logger.Warning(
                    "SchematicPlacer: place {0} lies in unloaded chunks but its rollback file names no "
                    + "readable position; it is kept for a later restore.", placeId);
                return false;
            }
            if (attempt >= MaxRestoreAttempts)
            {
                restoreWaiting.Remove(placeId);
                sapi.World.Logger.Error(
                    "SchematicPlacer: chunks of place {0} would not load after {1} tries; its rollback file is "
                    + "kept and the next server start restores it.", placeId, MaxRestoreAttempts);
                return false;
            }

            restoreWaiting.Add(placeId);
            if (attempt == 0)
                sapi.World.Logger.Notification(
                    "SchematicPlacer: place {0} lies in unloaded chunks; restoring once they are loaded.", placeId);

            // Margin only in dim 0: margin columns in other dims may never exist and never load.
            int margin = dim == 0 ? 1 : 0;
            void RequestLoad() => DimensionChunks.LoadArea(sapi,
                cx1 - margin, cz1 - margin, cx2 + margin, cz2 + margin, dim,
                () => TryRestore(placeId, attempt + 1),
                () => TryRestore(placeId, attempt + 1));

            if (attempt == 0) RequestLoad();
            else sapi.Event.RegisterCallback(_ => RequestLoad(), RestoreRetryDelayMs);
            return true;
        }

        public bool HasSavedData(long placeId)
        {
            return File.Exists(GetDataPath(placeId));
        }

        // Cleans up after an unclean shutdown. keepPlaceIds are persistent arenas that must stay placed.
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
                    try { File.Delete(file); } catch { } // corrupt or empty
                    continue;
                }

                long id = data.PlaceId;

                if (keepPlaceIds != null && keepPlaceIds.Contains(id)) continue;

                // After the block journal, or its schematic-era blocks land on restored terrain.
                EventManagers.eventBehavior.Shared.ArenaBlockJournal.RunAfterRestore(id, () => Restore(id));
            }
        }

        // Legacy "x,y,z" keys mean dim 0. A placement never spans dimensions.
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

        // Chunk X/Z range covering the saved blocks. 32-block chunks.
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
                // Floor, not x/32: integer division truncates toward zero for negative coords.
                int cx = (int)Math.Floor(x / 32.0), cz = (int)Math.Floor(z / 32.0);
                if (cx < cx1) cx1 = cx;
                if (cx > cx2) cx2 = cx;
                if (cz < cz1) cz1 = cz;
                if (cz > cz2) cz2 = cz;
                any = true;
            }
            return any;
        }

        // Atomic write: this file is the only record of the original terrain.
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
