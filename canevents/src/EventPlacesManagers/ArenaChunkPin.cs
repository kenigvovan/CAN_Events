using System;
using System.Collections.Generic;
using canevents.src.EventPlacesManagers.Places;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace canevents.src.EventPlacesManagers
{
    // Chunks only stay loaded near a player, so bot-only matches would lose their ground.
    // Pins are ref-counted per column.
    public static class ArenaChunkPin
    {
        private const int MaxColumns = 64;   // cap for runaway bounds

        private static readonly Dictionary<long, int> pinCounts = new Dictionary<long, int>();

        // Static counts would otherwise outlive the world in-process.
        public static void Reset()
        {
            lock (pinCounts) pinCounts.Clear();
        }

        // Caller keeps the returned columns for Release, so a changed arena can't orphan a pin.
        public static List<Vec2i> Pin(ICoreServerAPI sapi, EventPlace place)
        {
            var columns = ColumnsOf(place);
            if (sapi == null || columns.Count == 0) return columns;

            // Non-zero dims: load only. keepLoaded/unload only work in dim 0 and keys would collide.
            int dim = PlaceSummaryFormatter.Dimension(place);
            if (dim != 0)
            {
                foreach (var c in columns) DimensionChunks.Request(sapi, c.X, c.Y, dim);
                return new List<Vec2i>();
            }

            lock (pinCounts)
            {
                foreach (var c in columns)
                {
                    long key = Key(c);
                    pinCounts.TryGetValue(key, out int count);
                    pinCounts[key] = count + 1;
                    if (count == 0) sapi.WorldManager.LoadChunkColumn(c.X, c.Y, keepLoaded: true);
                }
            }
            return columns;
        }

        // Post-match work (rewards, corpses, teleport home) still needs the ground.
        private const int LingerSeconds = 60;

        private const int RetrySeconds = 30;

        // UnloadChunkColumn drops chunks even under a player, so keep a margin around anyone nearby.
        private const int NearbyColumns = 2;

        public static void Release(ICoreServerAPI sapi, List<Vec2i> columns)
        {
            if (sapi == null || columns == null) return;

            var expiring = new List<Vec2i>();
            lock (pinCounts)
            {
                foreach (var c in columns)
                {
                    long key = Key(c);
                    if (!pinCounts.TryGetValue(key, out int count)) continue;
                    if (count > 1) { pinCounts[key] = count - 1; continue; }

                    pinCounts.Remove(key);
                    expiring.Add(c);
                }
            }

            foreach (var c in expiring) ScheduleUnload(sapi, c, LingerSeconds);
        }

        private static void ScheduleUnload(ICoreServerAPI sapi, Vec2i column, int delaySeconds)
        {
            sapi.Event.RegisterCallback(_ =>
            {
                // Re-pinned by another match meanwhile.
                lock (pinCounts) if (pinCounts.ContainsKey(Key(column))) return;

                // No deadline: keep waiting while someone is near.
                if (SomeoneNear(sapi, column))
                {
                    ScheduleUnload(sapi, column, RetrySeconds);
                    return;
                }

                try { sapi.WorldManager.UnloadChunkColumn(column.X, column.Y); }
                catch (Exception e)
                {
                    sapi.World.Logger.Warning("[arena] Could not release chunk column {0}/{1}: {2}",
                        column.X, column.Y, e.Message);
                }
            }, delaySeconds * 1000);
        }

        private static bool SomeoneNear(ICoreServerAPI sapi, Vec2i column)
        {
            int size = GlobalConstants.ChunkSize;
            foreach (var p in sapi.World.AllOnlinePlayers)
            {
                var pos = p?.Entity?.Pos;
                if (pos == null) continue;

                int px = (int)Math.Floor(pos.X / size);
                int pz = (int)Math.Floor(pos.Z / size);

                // Dimension ignored: all dims share the X/Z map chunk that unloading drops.
                if (Math.Abs(px - column.X) <= NearbyColumns && Math.Abs(pz - column.Y) <= NearbyColumns)
                    return true;
            }
            return false;
        }

        private static List<Vec2i> ColumnsOf(EventPlace place)
        {
            var columns = new List<Vec2i>();
            if (place == null) return columns;

            int size = GlobalConstants.ChunkSize;

            if (place.HasBounds && place.BoundsMin != null && place.BoundsMax != null)
            {
                int minX = Column(Math.Min(place.BoundsMin.X, place.BoundsMax.X), size);
                int maxX = Column(Math.Max(place.BoundsMin.X, place.BoundsMax.X), size);
                int minZ = Column(Math.Min(place.BoundsMin.Z, place.BoundsMax.Z), size);
                int maxZ = Column(Math.Max(place.BoundsMin.Z, place.BoundsMax.Z), size);

                for (int x = minX; x <= maxX && columns.Count < MaxColumns; x++)
                    for (int z = minZ; z <= maxZ && columns.Count < MaxColumns; z++)
                        columns.Add(new Vec2i(x, z));

                if (columns.Count > 0) return columns;
            }

            // No bounds: spawn columns plus neighbours.
            if (place.spawnPositions != null)
            {
                foreach (var pos in place.spawnPositions)
                {
                    if (pos == null) continue;
                    for (int dx = -1; dx <= 1; dx++)
                        for (int dz = -1; dz <= 1; dz++)
                        {
                            var c = new Vec2i(Column(pos.X, size) + dx, Column(pos.Z, size) + dz);
                            if (columns.Count >= MaxColumns) return columns;
                            if (!columns.Contains(c)) columns.Add(c);
                        }
                }
            }
            return columns;
        }

        // Floor, not int division: -5/32 must be -1, not 0.
        private static int Column(int blockCoordinate, int size)
            => (int)Math.Floor((double)blockCoordinate / size);

        private static long Key(Vec2i c) => ((long)c.X << 32) ^ (uint)c.Y;
    }
}
