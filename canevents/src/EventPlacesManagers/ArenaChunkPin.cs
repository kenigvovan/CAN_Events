using System;
using System.Collections.Generic;
using canevents.src.EventPlacesManagers.Places;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace canevents.src.EventPlacesManagers
{
    /// <summary>
    /// Keeps an arena's chunks in memory for as long as a match is using it: chunks stay loaded only
    /// near a PLAYER, so an all-bot match would have its ground unload underneath it.
    /// Pins are counted per column, so matches sharing a column don't unload each other's ground.
    /// </summary>
    public static class ArenaChunkPin
    {
        private const int MaxColumns = 64;   // a runaway bounds must not pin half the world

        private static readonly Dictionary<long, int> pinCounts = new Dictionary<long, int>();

        /// <summary>
        /// Drops every pin without unloading anything (the world is going away). The counts are static
        /// and would otherwise outlive the world in-process, leaving stale pins that can never be released.
        /// </summary>
        public static void Reset()
        {
            lock (pinCounts) pinCounts.Clear();
        }

        /// <summary>
        /// Pins every chunk column the arena covers. Returns the columns to hand back to
        /// <see cref="Release"/>; the caller keeps them so a changed arena cannot orphan a pin.
        /// </summary>
        public static List<Vec2i> Pin(ICoreServerAPI sapi, EventPlace place)
        {
            var columns = ColumnsOf(place);
            if (sapi == null || columns.Count == 0) return columns;

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

        /// <summary>
        /// How long an arena stays loaded after its match ends, so post-match work (rewards, corpses,
        /// teleport home) still has ground under it.
        /// </summary>
        private const int LingerSeconds = 60;

        /// <summary>Re-check interval while somebody is still standing in the arena.</summary>
        private const int RetrySeconds = 30;

        /// <summary>
        /// How far around a column somebody still counts as "there", in chunk columns. One column is
        /// 32 blocks: checking only the exact column meant an admin looking at the arena from the
        /// next one over did not register, and the ground vanished in front of them. Unloading is
        /// unconditional (WorldAPI.UnloadChunkColumn drops the chunks whoever is standing on them),
        /// so this check is the only thing protecting anyone nearby.
        /// </summary>
        private const int NearbyColumns = 2;

        /// <summary>
        /// Drops this match's claim. A column is let go only once the last claim is gone, the linger has
        /// passed and nobody stands in it - unloading is unconditional and would drop the world under them.
        /// </summary>
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
                // Re-pinned in the meantime - another match took this arena, leave it alone.
                lock (pinCounts) if (pinCounts.ContainsKey(Key(column))) return;

                // Waits as long as it takes, with no deadline. There used to be one, after which the
                // column went regardless - which is exactly the case that matters: an admin standing
                // in the arena after a bot match, looking at it. Nothing is leaked by waiting: the
                // moment they walk away, the next check unloads it.
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

                // Integer division, so a negative coordinate rounds towards zero rather than down -
                // hence the floor. Getting this wrong puts the boundary in the wrong place west and
                // north of the origin, where most test worlds are not, which is how it survives.
                int px = (int)Math.Floor(pos.X / size);
                int pz = (int)Math.Floor(pos.Z / size);

                if (Math.Abs(px - column.X) <= NearbyColumns && Math.Abs(pz - column.Y) <= NearbyColumns)
                    return true;
            }
            return false;
        }

        /// <summary>The columns an arena occupies: its bounds when it has them, else its spawn points.</summary>
        private static List<Vec2i> ColumnsOf(EventPlace place)
        {
            var columns = new List<Vec2i>();
            if (place == null) return columns;

            int size = GlobalConstants.ChunkSize;

            if (place.HasBounds && place.BoundsMin != null && place.BoundsMax != null)
            {
                int minX = Math.Min(place.BoundsMin.X, place.BoundsMax.X) / size;
                int maxX = Math.Max(place.BoundsMin.X, place.BoundsMax.X) / size;
                int minZ = Math.Min(place.BoundsMin.Z, place.BoundsMax.Z) / size;
                int maxZ = Math.Max(place.BoundsMin.Z, place.BoundsMax.Z) / size;

                for (int x = minX; x <= maxX && columns.Count < MaxColumns; x++)
                    for (int z = minZ; z <= maxZ && columns.Count < MaxColumns; z++)
                        columns.Add(new Vec2i(x, z));

                if (columns.Count > 0) return columns;
            }

            // No bounds: pin the spawn columns plus neighbours, since a fight drifts a few blocks.
            if (place.spawnPositions != null)
            {
                foreach (var pos in place.spawnPositions)
                {
                    if (pos == null) continue;
                    for (int dx = -1; dx <= 1; dx++)
                        for (int dz = -1; dz <= 1; dz++)
                        {
                            var c = new Vec2i(pos.X / size + dx, pos.Z / size + dz);
                            if (columns.Count >= MaxColumns) return columns;
                            if (!columns.Contains(c)) columns.Add(c);
                        }
                }
            }
            return columns;
        }

        private static long Key(Vec2i c) => ((long)c.X << 32) ^ (uint)c.Y;
    }
}
