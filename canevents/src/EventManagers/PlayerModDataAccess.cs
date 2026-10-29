using System;
using System.Collections.Generic;
using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.Server;

namespace canevents.src.EventManagers
{
    // Read-only access to a player's ModData, online or not.
    // Order: online player -> ServerMain.GetWorldPlayerData (in memory) -> save DB via reflection.
    public static class PlayerModDataAccess
    {
        public enum Source { Online, Memory, Database, Unavailable }

        public static byte[] Read(ICoreServerAPI sapi, string uid, string key)
            => Read(sapi, uid, key, out _);

        // allowDatabase=false skips the DB read (it deserializes the whole player record on the main thread).
        public static byte[] Read(ICoreServerAPI sapi, string uid, string key, out Source source,
            bool allowDatabase = true)
        {
            source = Source.Unavailable;
            if (sapi == null || string.IsNullOrEmpty(uid) || string.IsNullOrEmpty(key)) return null;

            if (sapi.World.PlayerByUid(uid) is IServerPlayer online)
            {
                source = Source.Online;
                return online.GetModdata(key);
            }

            var data = WorldDataOf(sapi, uid, allowDatabase, out bool fromDb);
            if (data == null) return null;

            source = fromDb ? Source.Database : Source.Memory;
            try { return data.GetModdata(key); }
            catch (Exception) { source = Source.Unavailable; return null; }
        }

        // Two keys from one record, so an offline player's save is parsed once.
        public static (byte[] first, byte[] second) ReadPair(ICoreServerAPI sapi, string uid,
            string firstKey, string secondKey, out Source source, bool allowDatabase = true)
        {
            source = Source.Unavailable;
            if (sapi == null || string.IsNullOrEmpty(uid)) return (null, null);

            if (sapi.World.PlayerByUid(uid) is IServerPlayer online)
            {
                source = Source.Online;
                return (online.GetModdata(firstKey), online.GetModdata(secondKey));
            }

            var data = WorldDataOf(sapi, uid, allowDatabase, out bool fromDb);
            if (data == null) return (null, null);

            source = fromDb ? Source.Database : Source.Memory;
            try { return (data.GetModdata(firstKey), data.GetModdata(secondKey)); }
            catch (Exception) { source = Source.Unavailable; return (null, null); }
        }

        public static IEnumerable<(string uid, string name)> KnownPlayers(ICoreServerAPI sapi)
        {
            if (sapi == null) yield break;

            var seen = new HashSet<string>();

            foreach (var p in sapi.World.AllOnlinePlayers)
            {
                if (p?.PlayerUID == null || !seen.Add(p.PlayerUID)) continue;
                yield return (p.PlayerUID, p.PlayerName);
            }

            foreach (var kv in sapi.PlayerData.PlayerDataByUid)
            {
                if (kv.Key == null || !seen.Add(kv.Key)) continue;
                yield return (kv.Key, kv.Value?.LastKnownPlayername ?? kv.Key);
            }
        }

        public static string NameOf(ICoreServerAPI sapi, string uid)
        {
            if (sapi == null || string.IsNullOrEmpty(uid)) return uid;
            if (sapi.World.PlayerByUid(uid) is IServerPlayer online) return online.PlayerName;
            return sapi.PlayerData.GetPlayerDataByUid(uid)?.LastKnownPlayername ?? uid;
        }

        public static string UidOfName(ICoreServerAPI sapi, string name)
        {
            if (sapi == null || string.IsNullOrWhiteSpace(name)) return null;
            name = name.Trim();

            foreach (var p in sapi.World.AllOnlinePlayers)
                if (string.Equals(p?.PlayerName, name, StringComparison.OrdinalIgnoreCase))
                    return p.PlayerUID;

            return sapi.PlayerData.GetPlayerDataByLastKnownName(name)?.PlayerUID;
        }

        private static IWorldPlayerData WorldDataOf(ICoreServerAPI sapi, string uid, bool allowDatabase,
            out bool fromDb)
        {
            fromDb = false;

            // GetWorldPlayerData is only on ServerMain, not on the API interface.
            if (sapi.World is ServerMain server)
            {
                var live = server.GetWorldPlayerData(uid);
                if (live != null) return live;
            }

            if (!allowDatabase) return null;

            fromDb = true;
            return FromDatabase(sapi, uid);
        }

        // Reflection lookups are cached, including failures.
        private static bool dbProbed;
        private static object gameDatabase;
        private static MethodInfo getPlayerData;

        private static IWorldPlayerData FromDatabase(ICoreServerAPI sapi, string uid)
        {
            if (!ProbeDatabase(sapi)) return null;

            try
            {
                var raw = getPlayerData.Invoke(gameDatabase, new object[] { uid }) as byte[];
                if (raw == null || raw.Length == 0) return null;

                // Same as ConnectedClient does on rejoin.
                return SerializerUtil.Deserialize<ServerWorldPlayerData>(raw);
            }
            catch (Exception e)
            {
                sapi.World.Logger.VerboseDebug(
                    "[inventory] could not read saved player data for {0}: {1}", uid, e.Message);
                return null;
            }
        }

        private static bool ProbeDatabase(ICoreServerAPI sapi)
        {
            if (dbProbed) return getPlayerData != null && gameDatabase != null;
            dbProbed = true;

            try
            {
                if (!(sapi.World is ServerMain server)) return false;

                var chunkThreadField = typeof(ServerMain).GetField("chunkThread",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                var chunkThread = chunkThreadField?.GetValue(server);
                if (chunkThread == null) return false;

                var dbField = chunkThread.GetType().GetField("gameDatabase",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                gameDatabase = dbField?.GetValue(chunkThread);
                if (gameDatabase == null) return false;

                getPlayerData = gameDatabase.GetType().GetMethod("GetPlayerData",
                    BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string) }, null);
                return getPlayerData != null;
            }
            catch (Exception e)
            {
                sapi?.World?.Logger?.VerboseDebug(
                    "[inventory] saved-player-data lookup unavailable on this build: {0}", e.Message);
                gameDatabase = null;
                getPlayerData = null;
                return false;
            }
        }
    }
}
