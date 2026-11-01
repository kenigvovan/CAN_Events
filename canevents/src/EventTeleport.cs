using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using canevents.src.EventPlacesManagers;

namespace canevents.src
{
    // All player teleports go through here. Unlike plain TeleportToDouble it:
    // - loads the surrounding columns too (vanilla loads one, via (int)x / 32 which truncates toward zero)
    // - handles dimensions (TeleportToDouble uses Pos.SetPos, which never changes Pos.Dimension)
    // - logs server/client chunk state per hop, to debug "world didn't load, got kicked" reports
    public static class EventTeleport
    {
        private const int ChunkSize = 32;

        // Columns around the target to preload; 1 = 3x3 columns (96x96 blocks).
        private const int Ring = 1;

        private const int LandingCheckMs = 4000;

        // Disconnects within this window after a hop get logged as related.
        private const int SuspiciousDisconnectSeconds = 90;

        private sealed class Hop
        {
            public long AtMs;
            public string Reason;
            public double X, Y, Z;
            public int Dimension;
        }

        // Last hop per player uid.
        private static readonly Dictionary<string, Hop> lastHop = new Dictionary<string, Hop>();

        // uid -> the newest hop asked for. Only that one may still land: a waiting or loading older hop
        // would otherwise put the player back where they no longer belong (seat after the spawn).
        private static readonly Dictionary<string, long> newestRequest = new Dictionary<string, long>();
        private static long requestSeq;

        // A hop asked for while another is in flight waits for it in steps of this.
        private const int InFlightRetryMs = 250;

        // uid -> until when (server ms) a disconnect is the server's own doing (kick, test teardown),
        // so neither the disconnect nor the landing check reports it as a crash on arrival.
        private static readonly Dictionary<string, long> expectedGone = new Dictionary<string, long>();
        private const long ExpectedGoneMs = 10000;

        // Static, so clear it on world shutdown.
        public static void Reset()
        {
            lock (lastHop)
            {
                lastHop.Clear();
                expectedGone.Clear();
                newestRequest.Clear();
            }
        }

        // Call right before disconnecting a player on purpose.
        public static void ExpectDisconnect(ICoreServerAPI sapi, string uid)
        {
            if (sapi == null || uid == null) return;
            lock (lastHop) expectedGone[uid] = sapi.World.ElapsedMilliseconds + ExpectedGoneMs;
        }

        private static bool IsExpectedGone(ICoreServerAPI sapi, string uid)
        {
            lock (lastHop)
                return expectedGone.TryGetValue(uid, out long until) && sapi.World.ElapsedMilliseconds <= until;
        }

        private static Log LogOf(ICoreServerAPI sapi) => Log.For(sapi?.World?.Logger, "tp");

        // After this a new hop is allowed even if Teleporting is still set (worldgen can take seconds).
        private const long InFlightGraceMs = 15000;

        private static bool HopIsRecent(ICoreServerAPI sapi, string uid)
        {
            lock (lastHop)
            {
                if (!lastHop.TryGetValue(uid, out var hop)) return false;
                return sapi.World.ElapsedMilliseconds - hop.AtMs < InFlightGraceMs;
            }
        }

        // Floor, since (int)(-5) / 32 is 0, not -1.
        private static int ColumnOf(double coordinate) => (int)Math.Floor(coordinate / ChunkSize);

        // reason goes to the log. Returns false if no hop was scheduled.
        public static bool Send(ICoreServerAPI sapi, IServerPlayer player,
            double x, double y, double z, int dimension, string reason, Action onTeleported = null)
        {
            if (sapi == null || player?.Entity == null) return false;

            string uid = player.PlayerUID;
            long request;
            lock (lastHop) newestRequest[uid] = request = ++requestSeq;

            // One hop at a time: each landing resets CurrentChunkSentRadius, so stacked hops can keep the
            // client from ever finishing chunk loading. Dropping this one instead left a revived player at
            // the spectator seat with the hop to their spawn lost.
            if (InFlight(sapi, player))
            {
                LogOf(sapi).Notification("{0} [{1}]: a teleport is still in flight, this one waits for it",
                    player.PlayerName, reason);
                WaitThenHop(sapi, uid, x, y, z, dimension, reason, onTeleported, request);
                return true;
            }

            StartHop(sapi, player, x, y, z, dimension, reason, onTeleported, request);
            return true;
        }

        // Age check in case Teleporting gets stuck.
        private static bool InFlight(ICoreServerAPI sapi, IServerPlayer player)
            => player.Entity.Teleporting && HopIsRecent(sapi, player.PlayerUID);

        private static bool IsNewest(string uid, long request)
        {
            lock (lastHop) return newestRequest.TryGetValue(uid, out long newest) && newest == request;
        }

        private static void WaitThenHop(ICoreServerAPI sapi, string uid, double x, double y, double z,
            int dimension, string reason, Action onTeleported, long request)
        {
            sapi.Event.RegisterCallback(_ =>
            {
                if (!IsNewest(uid, request)) return;
                if (!(sapi.World.PlayerByUid(uid) is IServerPlayer player) || player.Entity == null) return;

                if (InFlight(sapi, player)) WaitThenHop(sapi, uid, x, y, z, dimension, reason, onTeleported, request);
                else StartHop(sapi, player, x, y, z, dimension, reason, onTeleported, request);
            }, InFlightRetryMs);
        }

        private static void StartHop(ICoreServerAPI sapi, IServerPlayer player,
            double x, double y, double z, int dimension, string reason, Action onTeleported, long request)
        {
            var log = LogOf(sapi);
            string uid = player.PlayerUID;
            var from = player.Entity.Pos;

            int cx = ColumnOf(x), cz = ColumnOf(z);
            bool serverHasIt = sapi.WorldManager.GetChunk(cx, ChunkYOf(y, dimension), cz) != null;

            log.Notification(
                "{0} [{1}]: {2:F1},{3:F1},{4:F1} dim{5} -> {6:F1},{7:F1},{8:F1} dim{9} (column {10}/{11}, on server: {12})",
                player.PlayerName, reason,
                from.X, from.Y, from.Z, from.Dimension,
                x, y, z, dimension,
                cx, cz, serverHasIt ? "loaded" : "NOT loaded, generating");

            lock (lastHop)
            {
                lastHop[uid] = new Hop
                {
                    AtMs = sapi.World.ElapsedMilliseconds,
                    Reason = reason,
                    X = x, Y = y, Z = z,
                    Dimension = dimension
                };
            }

            // OnLoaded fires once the whole rectangle is loaded.
            if (dimension == 0)
            {
                sapi.WorldManager.LoadChunkColumnPriority(cx - Ring, cz - Ring, cx + Ring, cz + Ring,
                    new ChunkLoadOptions
                    {
                        OnLoaded = () => Land(sapi, uid, x, y, z, dimension, reason, onTeleported, request)
                    });
                return;
            }

            // Other dims: wait only for the landing column (neighbors may never exist), just request the rest.
            for (int dx = -Ring; dx <= Ring; dx++)
                for (int dz = -Ring; dz <= Ring; dz++)
                    if (dx != 0 || dz != 0) DimensionChunks.Request(sapi, cx + dx, cz + dz, dimension);

            DimensionChunks.LoadColumns(sapi, new[] { (cx, cz) }, dimension,
                () => Land(sapi, uid, x, y, z, dimension, reason, onTeleported, request),
                () =>
                {
                    lock (lastHop) lastHop.Remove(uid);
                    log.Error("{0} [{1}]: column {2}/{3} of dimension {4} did not load within {5}s - no teleport "
                        + "(was it ever created?).", player.PlayerName, reason, cx, cz, dimension,
                        DimensionChunks.DefaultTimeoutMs / 1000);
                });
        }

        // Chunk rows of dimension d start at d*1024.
        private static int ChunkYOf(double y, int dimension)
            => DimensionChunks.ChunkY((int)Math.Floor(y / ChunkSize), dimension);

        public static bool Send(ICoreServerAPI sapi, IServerPlayer player, BlockPos pos,
            double offsetX, double offsetY, double offsetZ, string reason, Action onTeleported = null)
        {
            if (pos == null) return false;
            return Send(sapi, player, pos.X + offsetX, pos.Y + offsetY, pos.Z + offsetZ, pos.dimension,
                reason, onTeleported);
        }

        // Player may have left while chunks were loading.
        private static void Land(ICoreServerAPI sapi, string uid,
            double x, double y, double z, int dimension, string reason, Action onTeleported, long request)
        {
            var player = sapi.World.PlayerByUid(uid) as IServerPlayer;
            if (player?.Entity == null)
            {
                LogOf(sapi).Notification("uid {0} [{1}]: gone before the chunks were ready, no teleport",
                    uid, reason);
                return;
            }

            if (!IsNewest(uid, request))
            {
                LogOf(sapi).Notification("{0} [{1}]: a newer teleport was asked for while the chunks loaded, this one is dropped",
                    player.PlayerName, reason);
                return;
            }

            // TeleportToDouble can't change dimension.
            if (player.Entity.Pos.Dimension != dimension)
            {
                LogOf(sapi).Notification("{0} [{1}]: dimension {2} -> {3}",
                    player.PlayerName, reason, player.Entity.Pos.Dimension, dimension);
                player.Entity.ChangeDimension(dimension);
            }

            player.Entity.TeleportToDouble(x, y, z, onTeleported);

            sapi.Event.RegisterCallback(_ => CheckLanding(sapi, uid, x, y, z, dimension, reason), LandingCheckMs);
        }

        // Checks the player is still connected and the client actually has the chunk.
        private static void CheckLanding(ICoreServerAPI sapi, string uid,
            double x, double y, double z, int dimension, string reason)
        {
            var log = LogOf(sapi);
            var player = sapi.World.PlayerByUid(uid) as IServerPlayer;

            if (player?.Entity == null || player.ConnectionState == EnumClientState.Offline)
            {
                if (IsExpectedGone(sapi, uid)) return;
                log.Error("uid {0} [{1}]: GONE {2}s after the teleport to {3:F1},{4:F1},{5:F1} - "
                    + "this is the disconnect-on-arrival case", uid, reason, LandingCheckMs / 1000, x, y, z);
                return;
            }

            int cx = ColumnOf(x), cy = ChunkYOf(y, dimension), cz = ColumnOf(z);
            bool serverHasIt = sapi.WorldManager.GetChunk(cx, cy, cz) != null;
            bool clientHasIt = sapi.WorldManager.HasChunk(cx, cy, cz, player);
            var at = player.Entity.Pos;

            if (serverHasIt && clientHasIt)
            {
                log.Debug("{0} [{1}]: landed at {2:F1},{3:F1},{4:F1} dim{5}, chunk on both sides",
                    player.PlayerName, reason, at.X, at.Y, at.Z, at.Dimension);
                return;
            }

            log.Warning(
                "{0} [{1}]: {2}s after the teleport the chunk {3}/{4}/{5} is {6} on the server and {7} "
                + "on their client. They are at {8:F1},{9:F1},{10:F1} dim{11} (sent to {12:F1},{13:F1},{14:F1}). "
                + "A client with no chunk under it is what the 'map did not load, then I was kicked' "
                + "report looks like.",
                player.PlayerName, reason, LandingCheckMs / 1000, cx, cy, cz,
                serverHasIt ? "present" : "MISSING", clientHasIt ? "present" : "MISSING",
                at.X, at.Y, at.Z, at.Dimension, x, y, z);
        }

        // Logs disconnects that happen shortly after a teleport.
        public static void NoteDisconnect(ICoreServerAPI sapi, IServerPlayer player)
        {
            if (sapi == null || player == null) return;

            Hop hop;
            lock (lastHop)
            {
                // A hop still waiting must not fire after they log back in.
                newestRequest.Remove(player.PlayerUID);
                if (!lastHop.TryGetValue(player.PlayerUID, out hop)) return;
                lastHop.Remove(player.PlayerUID);
            }

            double secondsSince = (sapi.World.ElapsedMilliseconds - hop.AtMs) / 1000.0;
            if (secondsSince > SuspiciousDisconnectSeconds) return;
            if (IsExpectedGone(sapi, player.PlayerUID)) return;

            var at = player.Entity?.Pos;
            LogOf(sapi).Error(
                "{0} disconnected {1:F1}s after being teleported [{2}] to {3:F1},{4:F1},{5:F1} dim{6}. "
                + "Last known position {7}. Check the client's log for the reason it left - the server "
                + "did not ask it to.",
                player.PlayerName, secondsSince, hop.Reason, hop.X, hop.Y, hop.Z, hop.Dimension,
                at == null ? "unknown"
                    : string.Format("{0:F1},{1:F1},{2:F1} dim{3}", at.X, at.Y, at.Z, at.Dimension));
        }
    }
}
