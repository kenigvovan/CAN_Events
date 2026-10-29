using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace canevents.src.EventManagers.eventBehavior.Shared
{
    // Crash-safe block rollback per arena: savedBlocks/arena_<placeId>.json, one section per writer.
    // Leftovers from a crash are restored at next start once chunks load.
    public static class ArenaBlockJournal
    {
        public const string TagHgLoot         = "hg-loot";
        public const string TagSpleefCage     = "spleef-cage";
        public const string TagSpleefPlatform = "spleef-platform";
        public const string TagCtfPosts       = "ctf-posts";

        private const int FormatVersion = 1;
        private const int MaxLoadAttempts = 6;
        private const int RetryDelayMs = 5000;

        private sealed class Section
        {
            public string Tag;
            public Dictionary<string, int> Blocks = new Dictionary<string, int>();
        }

        private sealed class JournalData
        {
            public int Version = FormatVersion;
            public long PlaceId;
            public List<Section> Sections = new List<Section>();
        }

        private static ICoreServerAPI sapi;
        private static string folder;
        private static System.Func<long, bool> arenaInUse;

        private static readonly HashSet<long> pending = new HashSet<long>();
        private static readonly Dictionary<long, List<Action>> afterRestore = new Dictionary<long, List<Action>>();

        public static void Configure(ICoreServerAPI serverApi, string savedBlocksFolder, System.Func<long, bool> isArenaInUse)
        {
            sapi = serverApi;
            folder = savedBlocksFolder;
            arenaInUse = isArenaInUse;
            lock (pending) { pending.Clear(); afterRestore.Clear(); }
            try { if (!Directory.Exists(folder)) Directory.CreateDirectory(folder); }
            catch (Exception ex) { sapi?.World?.Logger?.Warning("[arena-journal] folder unavailable: {0}", ex.Message); }
        }

        // Same key format as the schematic rollback.
        public static string Key(BlockPos pos) => pos.X + "," + pos.Y + "," + pos.Z + "," + pos.dimension;

        // originals is overwritten with the first-recorded values.
        public static bool Record(long? placeId, string tag, Dictionary<BlockPos, int> originals)
        {
            if (placeId == null || originals == null) return true;

            var keyed = new Dictionary<string, int>();
            var posOf = new Dictionary<string, BlockPos>();
            foreach (var kvp in originals)
            {
                string key = Key(kvp.Key);
                keyed[key] = kvp.Value;
                posOf[key] = kvp.Key;
            }
            if (!Record(placeId.Value, tag, keyed)) return false;

            foreach (var kvp in keyed) originals[posOf[kvp.Key]] = kvp.Value;
            return true;
        }

        public static void RestoreOr(long? placeId, string tag, Action fallback)
        {
            if (placeId != null && Restore(placeId.Value, tag)) return;
            fallback?.Invoke();
        }

        public static void RestoreOr(long? placeId, string tag, IBlockAccessor ba, Dictionary<BlockPos, int> originals)
            => RestoreOr(placeId, tag, () =>
            {
                foreach (var kvp in originals) ba.SetBlock(kvp.Value, kvp.Key);
                ba.Commit();
            });

        // Pending arenas are not offered to new matches.
        public static bool IsPendingRestore(long placeId)
        {
            lock (pending) return pending.Contains(placeId);
        }

        public static void RunAfterRestore(long placeId, Action action)
        {
            if (action == null) return;
            lock (pending)
            {
                if (pending.Contains(placeId))
                {
                    if (!afterRestore.TryGetValue(placeId, out var list)) afterRestore[placeId] = list = new List<Action>();
                    list.Add(action);
                    return;
                }
            }
            action();
        }

        // Call before changing blocks. First recorded original wins across sections. False = don't touch the world.
        public static bool Record(long placeId, string tag, Dictionary<string, int> originals)
        {
            if (folder == null || originals == null) return false;
            if (originals.Count == 0) return true;

            var data = Load(placeId) ?? new JournalData { PlaceId = placeId };
            var section = data.Sections.FirstOrDefault(s => s.Tag == tag);
            if (section == null)
            {
                section = new Section { Tag = tag };
                data.Sections.Add(section);
            }

            foreach (var key in originals.Keys.ToList())
            {
                if (TryFindRecorded(data, key, out int first)) originals[key] = first;
                if (!section.Blocks.ContainsKey(key)) section.Blocks[key] = originals[key];
            }

            if (Save(data)) return true;
            sapi?.World?.Logger?.Error("[arena-journal] could not record '{0}' for place {1}; the blocks are left untouched.",
                tag, placeId);
            return false;
        }

        // True if handled now or deferred to chunk load; false if nothing recorded.
        public static bool Restore(long placeId, string tag)
        {
            if (sapi == null || folder == null) return false;
            var data = Load(placeId);
            var section = data?.Sections.FirstOrDefault(s => s.Tag == tag);
            if (section == null) return false;

            if (!AllChunksLoaded(new[] { section }))
            {
                sapi.World.Logger.Warning("[arena-journal] '{0}' of place {1} lies in unloaded chunks; "
                    + "restoring once they are loaded.", tag, placeId);
                ScheduleDeferredRestore(placeId, 0);
                return true;
            }

            WriteBack(section, placeId);
            data.Sections.Remove(section);
            SaveOrDelete(data);
            return true;
        }

        // Safety net after teardown.
        public static void RestoreRemaining(long placeId)
        {
            if (sapi == null || folder == null || IsPendingRestore(placeId)) return;
            var data = Load(placeId);
            if (data == null) return;
            if (data.Sections.Count == 0) { Delete(placeId); return; }

            if (!AllChunksLoaded(data.Sections))
            {
                ScheduleDeferredRestore(placeId, 0);
                return;
            }

            sapi.World.Logger.Warning("[arena-journal] place {0} still had {1} unrestored section(s) after its "
                + "match ({2}) - restoring them now.", placeId, data.Sections.Count,
                string.Join(", ", data.Sections.Select(s => s.Tag)));
            RestoreAllSections(data);
        }

        // Startup sweep. Must run before the schematic sweep, which waits on it.
        public static void RestoreAllOrphaned()
        {
            if (sapi == null || folder == null) return;

            MigrateLegacyHgLoot();

            string[] files;
            try { files = Directory.GetFiles(folder, "arena_*.json"); }
            catch (Exception ex) { sapi.World.Logger.Warning("[arena-journal] orphan sweep skipped: {0}", ex.Message); return; }
            if (files.Length == 0) return;

            sapi.World.Logger.Notification("[arena-journal] {0} arena(s) left modified by an unclean shutdown - restoring", files.Length);

            foreach (var file in files)
            {
                JournalData data;
                try { data = JsonConvert.DeserializeObject<JournalData>(File.ReadAllText(file)); }
                catch { data = null; }

                if (data?.Sections == null || data.Sections.All(s => s?.Blocks == null || s.Blocks.Count == 0))
                {
                    try { File.Delete(file); } catch { }
                    continue;
                }

                // Any dimension: the deferred restore loads the columns through DimensionChunks.
                ScheduleDeferredRestore(data.PlaceId, 0);
            }
        }

        private static void ScheduleDeferredRestore(long placeId, int attempt)
        {
            lock (pending) pending.Add(placeId);

            var data = Load(placeId);
            if (data == null || !TryGetChunkBounds(data.Sections, out int cx1, out int cz1, out int cx2, out int cz2))
            {
                FinishDeferred(placeId);
                return;
            }

            // Margin only in dim 0: a never-created column in other dims never loads.
            int dim = DimensionOf(data.Sections);
            int margin = dim == 0 ? 1 : 0;
            EventPlacesManagers.DimensionChunks.LoadArea(sapi,
                cx1 - margin, cz1 - margin, cx2 + margin, cz2 + margin, dim,
                () => TryDeferredRestore(placeId, attempt),
                () => TryDeferredRestore(placeId, attempt));
        }

        private static void TryDeferredRestore(long placeId, int attempt)
        {
            try
            {
                if (arenaInUse != null && arenaInUse(placeId))
                {
                    sapi.Event.RegisterCallback(_ => TryDeferredRestore(placeId, attempt), RetryDelayMs);
                    return;
                }

                var data = Load(placeId);
                if (data == null || data.Sections.Count == 0)
                {
                    if (data != null) Delete(placeId);
                    FinishDeferred(placeId);
                    return;
                }

                if (!AllChunksLoaded(data.Sections))
                {
                    if (attempt + 1 < MaxLoadAttempts)
                    {
                        sapi.Event.RegisterCallback(_ => ScheduleDeferredRestore(placeId, attempt + 1), RetryDelayMs);
                        return;
                    }

                    sapi.World.Logger.Error("[arena-journal] chunks of place {0} would not load after {1} tries; "
                        + "journal kept for the next start or the arena's next match.", placeId, MaxLoadAttempts);
                    FinishDeferred(placeId);
                    return;
                }

                RestoreAllSections(data);
                FinishDeferred(placeId);
            }
            catch (Exception e)
            {
                sapi?.World?.Logger?.Error("[arena-journal] restore of place {0} failed: {1}", placeId, e);
                FinishDeferred(placeId);
            }
        }

        private static void FinishDeferred(long placeId)
        {
            List<Action> actions;
            lock (pending)
            {
                pending.Remove(placeId);
                if (afterRestore.TryGetValue(placeId, out actions)) afterRestore.Remove(placeId);
            }
            if (actions == null) return;
            foreach (var action in actions)
            {
                try { action(); }
                catch (Exception e) { sapi?.World?.Logger?.Error("[arena-journal] follow-up after restoring place {0} failed: {1}", placeId, e); }
            }
        }

        // Newest first, so on overlaps the oldest original wins.
        private static void RestoreAllSections(JournalData data)
        {
            for (int i = data.Sections.Count - 1; i >= 0; i--)
                WriteBack(data.Sections[i], data.PlaceId);
            data.Sections.Clear();
            Delete(data.PlaceId);
        }

        private static void WriteBack(Section section, long placeId)
        {
            var ba = sapi.World.BlockAccessor;
            int skipped = 0;
            foreach (var kvp in section.Blocks)
            {
                if (!TryParseKey(kvp.Key, out var pos)) { skipped++; continue; }
                ba.SetBlock(kvp.Value, pos);
            }
            ba.Commit();

            if (skipped > 0)
                sapi.World.Logger.Warning("[arena-journal] '{0}' of place {1}: skipped {2} unreadable key(s).",
                    section.Tag, placeId, skipped);
            sapi.World.Logger.Notification("[arena-journal] Restored {0} block(s) of '{1}' on place {2}.",
                section.Blocks.Count - skipped, section.Tag, placeId);
        }

        // SetBlock into an unloaded chunk silently does nothing.
        private static bool AllChunksLoaded(IEnumerable<Section> sections)
        {
            var ba = sapi.World.BlockAccessor;
            foreach (var section in sections)
                foreach (var key in section.Blocks.Keys)
                    if (TryParseKey(key, out var pos) && ba.GetChunkAtBlockPos(pos) == null) return false;
            return true;
        }

        private static bool TryFindRecorded(JournalData data, string key, out int blockId)
        {
            foreach (var s in data.Sections)
                if (s.Blocks.TryGetValue(key, out blockId)) return true;
            blockId = 0;
            return false;
        }

        private static string GetPath(long placeId) => Path.Combine(folder, "arena_" + placeId + ".json");

        private static JournalData Load(long placeId)
        {
            string path = GetPath(placeId);
            if (!File.Exists(path)) return null;
            try
            {
                var data = JsonConvert.DeserializeObject<JournalData>(File.ReadAllText(path));
                if (data == null) return null;
                data.Sections ??= new List<Section>();
                data.Sections.RemoveAll(s => s == null);
                foreach (var s in data.Sections) s.Blocks ??= new Dictionary<string, int>();
                data.PlaceId = placeId;
                return data;
            }
            catch (Exception e)
            {
                sapi?.World?.Logger?.Error("[arena-journal] journal of place {0} is unreadable: {1}", placeId, e.Message);
                return null;
            }
        }

        private static bool Save(JournalData data)
        {
            if (Scoring.JsonStore.TryWriteAtomic(GetPath(data.PlaceId), data, out var error)) return true;
            sapi?.World?.Logger?.Error("[arena-journal] failed to write journal of place {0}: {1}", data.PlaceId, error);
            return false;
        }

        private static void SaveOrDelete(JournalData data)
        {
            if (data.Sections.Count == 0) Delete(data.PlaceId);
            else Save(data);
        }

        private static void Delete(long placeId)
        {
            try { string path = GetPath(placeId); if (File.Exists(path)) File.Delete(path); }
            catch (Exception e) { sapi?.World?.Logger?.Warning("[arena-journal] could not delete journal of place {0}: {1}", placeId, e.Message); }
        }

        // TODO(remove in the release after 1.2.0): one-time conversion of legacy hgloot_<placeId>.json.
        private sealed class LegacyHgLootData
        {
            public long PlaceId;
            public Dictionary<string, int> SavedBlocks;
        }

        private static void MigrateLegacyHgLoot()
        {
            string[] files;
            try { files = Directory.GetFiles(folder, "hgloot_*.json"); }
            catch { return; }

            foreach (var file in files)
            {
                LegacyHgLootData legacy;
                try { legacy = JsonConvert.DeserializeObject<LegacyHgLootData>(File.ReadAllText(file)); }
                catch { legacy = null; }

                if (legacy?.SavedBlocks == null || legacy.SavedBlocks.Count == 0)
                {
                    try { File.Delete(file); } catch { }
                    continue;
                }

                if (!Record(legacy.PlaceId, TagHgLoot, legacy.SavedBlocks)) continue;
                try { File.Delete(file); } catch { }
                sapi.World.Logger.Notification("[arena-journal] converted legacy Hunger Games loot rollback of place {0}.",
                    legacy.PlaceId);
            }
        }

        private static bool TryParseKey(string key, out BlockPos pos)
        {
            pos = null;
            if (key == null) return false;
            var parts = key.Split(',');
            if (parts.Length < 3
                || !int.TryParse(parts[0], out int x)
                || !int.TryParse(parts[1], out int y)
                || !int.TryParse(parts[2], out int z))
                return false;
            int dim = 0;
            if (parts.Length >= 4 && !int.TryParse(parts[3], out dim)) dim = 0;
            pos = new BlockPos(x, y, z, dim);
            return true;
        }

        private static int DimensionOf(IEnumerable<Section> sections)
        {
            foreach (var s in sections)
                foreach (var key in s.Blocks.Keys)
                    if (TryParseKey(key, out var pos)) return pos.dimension;
            return 0;
        }

        private static bool TryGetChunkBounds(IEnumerable<Section> sections, out int cx1, out int cz1, out int cx2, out int cz2)
        {
            cx1 = cz1 = int.MaxValue;
            cx2 = cz2 = int.MinValue;
            bool any = false;
            int size = Vintagestory.API.Config.GlobalConstants.ChunkSize;
            foreach (var s in sections)
            {
                foreach (var key in s.Blocks.Keys)
                {
                    if (!TryParseKey(key, out var pos)) continue;
                    // Floor, not truncation: negative coordinates.
                    int cx = (int)Math.Floor((double)pos.X / size), cz = (int)Math.Floor((double)pos.Z / size);
                    if (cx < cx1) cx1 = cx;
                    if (cx > cx2) cx2 = cx;
                    if (cz < cz1) cz1 = cz;
                    if (cz > cz2) cz2 = cz;
                    any = true;
                }
            }
            return any;
        }
    }
}
