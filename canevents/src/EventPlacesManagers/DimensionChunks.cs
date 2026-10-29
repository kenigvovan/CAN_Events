using System;
using System.Collections.Generic;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace canevents.src.EventPlacesManagers
{
    // LoadChunkColumnPriority only fills dim 0. Other dims need LoadChunkColumnForDimension, which has
    // no callback, so we poll. As of VS 1.22.7 it only reads from the save and never generates, so a
    // column that was never created just times out. Vanilla never unloads non-zero dims.
    public static class DimensionChunks
    {
        public const int DefaultTimeoutMs = 10000;
        private const int PollMs = 100;
        private const int DimensionChunkRows = 1024;   // 32768 blocks per dimension / 32

        // Floors, so -5 is column -1, not 0.
        public static int ColumnOf(double blockCoordinate)
            => (int)Math.Floor(blockCoordinate / GlobalConstants.ChunkSize);

        public static int ChunkY(int localChunkY, int dim) => localChunkY + dim * DimensionChunkRows;

        public static bool IsLoaded(ICoreServerAPI sapi, BlockPos pos)
        {
            if (sapi == null || pos == null) return false;
            // InternalY = Y + dim*32768, so /32 is already the dimension's row.
            int cy = (int)Math.Floor((double)pos.InternalY / GlobalConstants.ChunkSize);
            return sapi.WorldManager.GetChunk(ColumnOf(pos.X), cy, ColumnOf(pos.Z)) != null;
        }

        public static bool IsColumnLoaded(ICoreServerAPI sapi, int cx, int cz, int dim)
        {
            int rows = Math.Max(1, sapi.WorldManager.MapSizeY / GlobalConstants.ChunkSize);
            for (int cy = 0; cy < rows; cy++)
                if (sapi.WorldManager.GetChunk(cx, ChunkY(cy, dim), cz) == null) return false;
            return true;
        }

        public static void Request(ICoreServerAPI sapi, int cx, int cz, int dim)
        {
            if (sapi == null) return;
            if (dim == 0) { sapi.WorldManager.LoadChunkColumnPriority(cx, cz); return; }
            // Otherwise a loaded column gets re-read from disk and thrown away.
            if (!IsColumnLoaded(sapi, cx, cz, dim)) sapi.WorldManager.LoadChunkColumnForDimension(cx, cz, dim);
        }

        // onLoaded runs on the main thread. In dim 0 there is no timeout, so onTimeout never fires.
        public static void LoadArea(ICoreServerAPI sapi, int cx1, int cz1, int cx2, int cz2, int dim,
            Action onLoaded, Action onTimeout = null, int timeoutMs = DefaultTimeoutMs)
        {
            if (sapi == null) return;
            if (dim == 0)
            {
                sapi.WorldManager.LoadChunkColumnPriority(cx1, cz1, cx2, cz2,
                    onLoaded == null ? null : new ChunkLoadOptions { OnLoaded = onLoaded });
                return;
            }

            var columns = new List<(int cx, int cz)>();
            for (int x = Math.Min(cx1, cx2); x <= Math.Max(cx1, cx2); x++)
                for (int z = Math.Min(cz1, cz2); z <= Math.Max(cz1, cz2); z++)
                    columns.Add((x, z));
            LoadColumns(sapi, columns, dim, onLoaded, onTimeout, timeoutMs);
        }

        // In dim 0 several columns load as their bounding rectangle.
        public static void LoadColumns(ICoreServerAPI sapi, IEnumerable<(int cx, int cz)> columns, int dim,
            Action onLoaded, Action onTimeout = null, int timeoutMs = DefaultTimeoutMs)
        {
            if (sapi == null || columns == null) return;

            var list = new List<(int cx, int cz)>(columns);
            if (list.Count == 0)
            {
                if (onLoaded != null) sapi.Event.EnqueueMainThreadTask(onLoaded, "canevents-dimchunks");
                return;
            }

            if (dim == 0 && list.Count == 1)
            {
                // Runs OnLoaded synchronously if already loaded.
                sapi.WorldManager.LoadChunkColumnPriority(list[0].cx, list[0].cz,
                    onLoaded == null ? null : new ChunkLoadOptions { OnLoaded = onLoaded });
                return;
            }

            if (dim == 0)
            {
                int x1 = int.MaxValue, z1 = int.MaxValue, x2 = int.MinValue, z2 = int.MinValue;
                foreach (var c in list)
                {
                    x1 = Math.Min(x1, c.cx); x2 = Math.Max(x2, c.cx);
                    z1 = Math.Min(z1, c.cz); z2 = Math.Max(z2, c.cz);
                }
                LoadArea(sapi, x1, z1, x2, z2, 0, onLoaded, onTimeout, timeoutMs);
                return;
            }

            foreach (var c in list) Request(sapi, c.cx, c.cz, dim);
            if (onLoaded == null && onTimeout == null) return;

            long deadline = sapi.World.ElapsedMilliseconds + Math.Max(0, timeoutMs);

            // Never re-entrant, even when already loaded: callers expect OnLoaded later.
            void Poll(float _)
            {
                bool all = true;
                foreach (var c in list)
                    if (!IsColumnLoaded(sapi, c.cx, c.cz, dim)) { all = false; break; }

                if (all) { onLoaded?.Invoke(); return; }
                if (sapi.World.ElapsedMilliseconds >= deadline) { onTimeout?.Invoke(); return; }
                sapi.Event.RegisterCallback(Poll, PollMs);
            }
            sapi.Event.RegisterCallback(Poll, PollMs);
        }
    }
}
