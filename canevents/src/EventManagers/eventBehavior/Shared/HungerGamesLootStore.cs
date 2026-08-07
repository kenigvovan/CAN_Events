using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace canevents.src.EventManagers.eventBehavior.Shared
{
    /// <summary>
    /// Crash-safe rollback for Hunger Games loot chests, mirroring <c>SchematicPlacer</c>: overwritten
    /// blocks are snapshotted to <c>hgloot_&lt;placeId&gt;.json</c> BEFORE the world is touched, and a
    /// startup sweep puts them back. The distinct <c>hgloot_</c> prefix keeps this out of the schematic
    /// sweep's <c>placed_*.json</c> glob. Static (folder + boot sweep are process-wide); keying by
    /// placeId keeps concurrent arenas independent.
    /// </summary>
    public static class HungerGamesLootStore
    {
        private sealed class RollbackData
        {
            public long PlaceId;
            public Dictionary<string, int> SavedBlocks = new Dictionary<string, int>();
        }

        private static ICoreServerAPI sapi;
        private static string folder;

        /// <summary>Call once when the server API is available (see canevents.cs).</summary>
        public static void Configure(ICoreServerAPI serverApi, string savedBlocksFolder)
        {
            sapi = serverApi;
            folder = savedBlocksFolder;
            try { if (!Directory.Exists(folder)) Directory.CreateDirectory(folder); }
            catch (Exception ex) { sapi?.World?.Logger?.Warning("[HG] loot store folder unavailable: {0}", ex.Message); }
        }

        /// <summary>"x,y,z,dim", the same key shape the schematic rollback uses.</summary>
        public static string Key(BlockPos pos) => pos.X + "," + pos.Y + "," + pos.Z + "," + pos.dimension;

        /// <summary>
        /// Persists the pre-placement snapshot. On false the caller must place no chests: a chest with
        /// no record of what was under it means that block is lost for good.
        /// </summary>
        public static bool Save(long placeId, Dictionary<string, int> savedBlocks)
        {
            if (folder == null) return false;
            var data = new RollbackData { PlaceId = placeId, SavedBlocks = savedBlocks };
            if (Scoring.JsonStore.TryWriteAtomic(GetPath(placeId), data, out var error)) return true;
            sapi?.World?.Logger?.Error("[HG] failed to save loot rollback for place {0}: {1}", placeId, error);
            return false;
        }

        /// <summary>Puts back every block a chest replaced, then deletes the file. No-op if none.</summary>
        public static bool Restore(long placeId)
        {
            if (sapi == null) return false;
            var data = Load(placeId);
            if (data == null) return false;

            var ba = sapi.World.BlockAccessor;
            int skipped = 0;
            foreach (var kvp in data.SavedBlocks)
            {
                if (!TryParseKey(kvp.Key, out int x, out int y, out int z, out int dim)) { skipped++; continue; }
                ba.SetBlock(kvp.Value, new BlockPos(x, y, z, dim));
            }
            if (skipped > 0)
                sapi.World.Logger.Warning("[HG] loot restore for place {0} skipped {1} unreadable key(s).", placeId, skipped);

            ba.Commit();
            Delete(placeId);
            return true;
        }

        /// <summary>
        /// Restores chests left placed by an unclean shutdown, loading the affected chunks first (the
        /// area is usually unloaded at startup). Best-effort; call once the world is running.
        /// </summary>
        public static void RestoreAllOrphaned()
        {
            if (sapi == null || folder == null) return;

            string[] files;
            try { files = Directory.GetFiles(folder, "hgloot_*.json"); }
            catch (Exception ex) { sapi.World.Logger.Warning("[HG] loot orphan sweep skipped: {0}", ex.Message); return; }
            if (files.Length == 0) return;

            sapi.World.Logger.Notification("[HG] {0} orphaned loot set(s) from an unclean shutdown - restoring", files.Length);

            foreach (var file in files)
            {
                RollbackData data;
                try { data = JsonConvert.DeserializeObject<RollbackData>(File.ReadAllText(file)); }
                catch { data = null; }

                if (data == null || data.SavedBlocks == null || data.SavedBlocks.Count == 0
                    || !TryGetChunkBounds(data.SavedBlocks, out int cx1, out int cz1, out int cx2, out int cz2))
                {
                    try { File.Delete(file); } catch { } // corrupt/empty, drop so it can't linger
                    continue;
                }

                long id = data.PlaceId;

                // LoadChunkColumnPriority only reaches dimension 0, and Restore deletes the file
                // unconditionally, so another dimension would lose the record. Keep it for manual restore.
                int dim = DimensionOf(data.SavedBlocks);
                if (dim != 0)
                {
                    sapi.World.Logger.Warning(
                        "[HG] loot for place {0} was left in dimension {1}; the startup sweep only loads "
                        + "dimension 0, so its rollback file is kept for a manual restore.", id, dim);
                    continue;
                }

                sapi.WorldManager.LoadChunkColumnPriority(cx1 - 1, cz1 - 1, cx2 + 1, cz2 + 1, new ChunkLoadOptions
                {
                    OnLoaded = () => Restore(id)
                });
            }
        }

        private static RollbackData Load(long placeId)
        {
            string path = GetPath(placeId);
            if (!File.Exists(path)) return null;
            try { return JsonConvert.DeserializeObject<RollbackData>(File.ReadAllText(path)); }
            catch { return null; }
        }

        private static void Delete(long placeId)
        {
            try { string path = GetPath(placeId); if (File.Exists(path)) File.Delete(path); }
            catch { }
        }

        private static string GetPath(long placeId) => Path.Combine(folder, "hgloot_" + placeId + ".json");

        private static bool TryParseKey(string key, out int x, out int y, out int z, out int dim)
        {
            x = y = z = dim = 0;
            var parts = key.Split(',');
            if (parts.Length < 3
                || !int.TryParse(parts[0], out x)
                || !int.TryParse(parts[1], out y)
                || !int.TryParse(parts[2], out z))
                return false;
            if (parts.Length >= 4 && !int.TryParse(parts[3], out dim)) dim = 0;
            return true;
        }

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

        // Min/max chunk (X,Z) covering the saved positions ("x,y,z,dim" keys). 32-block chunks.
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
                // Floor division: plain x/32 truncates toward zero, mapping x=-5 to chunk 0 instead of -1,
                // so a negative-coordinate arena would load the wrong columns.
                int cx = (int)Math.Floor(x / 32.0), cz = (int)Math.Floor(z / 32.0);
                if (cx < cx1) cx1 = cx;
                if (cx > cx2) cx2 = cx;
                if (cz < cz1) cz1 = cz;
                if (cz > cz2) cz2 = cz;
                any = true;
            }
            return any;
        }
    }
}
