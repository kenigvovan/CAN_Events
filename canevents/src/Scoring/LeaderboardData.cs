using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Server;
using Newtonsoft.Json;

namespace canevents.src.Scoring
{
    public class PlayerStats
    {
        public int Wins { get; set; }
        public int Losses { get; set; }
        public int Draws { get; set; }
        public int Kills { get; set; }
        public int Deaths { get; set; }
        public int EventsPlayed { get; set; }
        // Elo per (player, eventCode). Default also covers pre-Elo saves. Add() doesn't sum it.
        public int Rating { get; set; } = EloCalculator.DefaultRating;

        [JsonIgnore]
        public float KDRatio => Deaths > 0 ? (float)Kills / Deaths : Kills;

        [JsonIgnore]
        public float WinRate => EventsPlayed > 0 ? (float)Wins / EventsPlayed * 100f : 0f;

        public void Add(PlayerStats other)
        {
            Wins += other.Wins;
            Losses += other.Losses;
            Draws += other.Draws;
            Kills += other.Kills;
            Deaths += other.Deaths;
            EventsPlayed += other.EventsPlayed;
        }
    }

    public class LeaderboardData
    {
        private const string SaveKey = "canevents_leaderboard_v2";
        private const string OldSaveKey = "canevents_leaderboard";

        // playerUid -> eventCode -> stats
        private Dictionary<string, Dictionary<string, PlayerStats>> stats = new();
        private ICoreServerAPI sapi;
        private bool savePending = false;

        private bool disposed = false;

        public LeaderboardData(ICoreServerAPI sapi)
        {
            this.sapi = sapi;
            Load();
            sapi.Event.GameWorldSave += FlushSave;
            sapi.Event.ServerRunPhase(EnumServerRunPhase.Shutdown, FlushSave);
        }

        // ServerRunPhase(Shutdown) can't be unregistered; the disposed flag stops a stale instance from saving.
        public void Dispose()
        {
            if (disposed) return;
            FlushSave();
            disposed = true;
            sapi.Event.GameWorldSave -= FlushSave;
        }

        private PlayerStats GetStats(string playerUid, string eventCode)
        {
            if (!stats.TryGetValue(playerUid, out var perEvent))
            {
                perEvent = new Dictionary<string, PlayerStats>();
                stats[playerUid] = perEvent;
            }
            if (!perEvent.TryGetValue(eventCode, out var s))
            {
                s = new PlayerStats();
                perEvent[eventCode] = s;
            }
            return s;
        }

        public void RecordWin(string playerUid, string eventCode)
        {
            var s = GetStats(playerUid, eventCode);
            s.Wins++;
            s.EventsPlayed++;
            ScheduleSave();
        }

        public void RecordLoss(string playerUid, string eventCode)
        {
            var s = GetStats(playerUid, eventCode);
            s.Losses++;
            s.EventsPlayed++;
            ScheduleSave();
        }

        public void RecordDraw(string playerUid, string eventCode)
        {
            var s = GetStats(playerUid, eventCode);
            s.Draws++;
            s.EventsPlayed++;
            ScheduleSave();
        }

        public void RecordKill(string playerUid, string eventCode)
        {
            GetStats(playerUid, eventCode).Kills++;
            ScheduleSave();
        }

        public void RecordDeath(string playerUid, string eventCode)
        {
            GetStats(playerUid, eventCode).Deaths++;
            ScheduleSave();
        }

        // sides[i] = starting roster incl. disconnected; ranks: lower is better, equal = tie.
        public void RecordEloResult(string eventCode, IReadOnlyList<IReadOnlyList<string>> sides, IReadOnlyList<int> ranks)
        {
            if (string.IsNullOrEmpty(eventCode) || sides == null || ranks == null
                || sides.Count < 2 || ranks.Count != sides.Count) return;
            if (sides.Any(s => s == null || s.Count == 0)) return;

            var ratings = sides.Select(side => side.Select(uid => GetStats(uid, eventCode).Rating).ToArray()).ToArray();
            var deltas = EloCalculator.ComputeRanked(ratings, ranks.ToArray());

            for (int i = 0; i < sides.Count; i++)
                foreach (var uid in sides[i]) GetStats(uid, eventCode).Rating += deltas[i];
            ScheduleSave();
        }

        public int GetRating(string playerUid, string eventCode)
        {
            if (string.IsNullOrEmpty(playerUid) || string.IsNullOrEmpty(eventCode))
                return EloCalculator.DefaultRating;
            if (stats.TryGetValue(playerUid, out var perEvent) && perEvent.TryGetValue(eventCode, out var s))
                return s.Rating;
            return EloCalculator.DefaultRating;
        }

        private void ScheduleSave()
        {
            if (savePending) return;
            savePending = true;
            sapi.Event.RegisterCallback(_ => FlushSave(), 5000);
        }

        private void FlushSave()
        {
            if (disposed || !savePending) return;
            savePending = false;
            Save();
        }

        // null eventCode = all events combined.
        public Dictionary<string, PlayerStats> GetAllStats(string eventCode = null)
        {
            var result = new Dictionary<string, PlayerStats>();
            foreach (var kvp in stats)
            {
                var playerUid = kvp.Key;
                var aggregated = new PlayerStats();

                foreach (var evKvp in kvp.Value)
                {
                    // Case-insensitive, same as FindEventByCode.
                    if (!string.IsNullOrEmpty(eventCode)
                        && !string.Equals(evKvp.Key, eventCode, System.StringComparison.OrdinalIgnoreCase)) continue;
                    aggregated.Add(evKvp.Value);
                    // Rating only makes sense per event.
                    if (!string.IsNullOrEmpty(eventCode)) aggregated.Rating = evKvp.Value.Rating;
                }

                if (aggregated.EventsPlayed > 0 || aggregated.Kills > 0 || aggregated.Deaths > 0)
                {
                    result[playerUid] = aggregated;
                }
            }
            return result;
        }

        // Live records, not copies.
        public IReadOnlyDictionary<string, PlayerStats> GetPlayerStatsByEvent(string playerUid)
        {
            if (playerUid != null && stats.TryGetValue(playerUid, out var perEvent))
                return perEvent;
            return new Dictionary<string, PlayerStats>();
        }

        public List<string> GetEventCodes()
        {
            var codes = new HashSet<string>();
            foreach (var perEvent in stats.Values)
            {
                foreach (var code in perEvent.Keys)
                {
                    codes.Add(code);
                }
            }
            return codes.ToList();
        }

        private void Load()
        {
            // Kept for the corrupt-data backup.
            byte[] raw = null;
            string rawKey = null;
            try
            {
                byte[] data = sapi.WorldManager.SaveGame.GetData(SaveKey);
                raw = data;
                rawKey = SaveKey;
                if (data != null)
                {
                    string json = System.Text.Encoding.UTF8.GetString(data);
                    stats = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, PlayerStats>>>(json)
                        ?? new();
                    sapi.World.Logger.Notification("LeaderboardData: Loaded {0} player records (v2)", stats.Count);
                    return;
                }

                // Migrate old flat Dictionary<playerUid, PlayerStats> under the "legacy" event code.
                byte[] oldData = sapi.WorldManager.SaveGame.GetData(OldSaveKey);
                raw = oldData;
                rawKey = OldSaveKey;
                if (oldData != null)
                {
                    string json = System.Text.Encoding.UTF8.GetString(oldData);
                    var oldStats = JsonConvert.DeserializeObject<Dictionary<string, PlayerStats>>(json);
                    if (oldStats != null)
                    {
                        foreach (var kvp in oldStats)
                        {
                            stats[kvp.Key] = new Dictionary<string, PlayerStats> { { "legacy", kvp.Value } };
                        }
                        Save();
                        sapi.World.Logger.Notification("LeaderboardData: Migrated {0} old records to v2", oldStats.Count);
                    }
                }
            }
            catch (System.Exception e)
            {
                // Back up before starting empty, the next save would overwrite it.
                string kept = BackUpCorrupt(raw, rawKey);
                sapi.World.Logger.Error("LeaderboardData: failed to load ({0}), starting EMPTY. {1}",
                    e.Message, kept ?? "The unreadable data could NOT be backed up.");
                stats = new();
            }
        }

        private string BackUpCorrupt(byte[] raw, string key)
        {
            if (raw == null || key == null) return null;
            string stamp = System.DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            var kept = new List<string>();

            string blobKey = key + ".corrupt-" + stamp;
            try
            {
                sapi.WorldManager.SaveGame.StoreData(blobKey, raw);
                kept.Add("save key '" + blobKey + "'");
            }
            catch { }

            try
            {
                var dir = ModPaths.Root;
                System.IO.Directory.CreateDirectory(dir);
                string file = System.IO.Path.Combine(dir, "leaderboard.json.corrupt-" + stamp);
                System.IO.File.WriteAllBytes(file, raw);
                kept.Add("file " + file);
            }
            catch { }

            return kept.Count == 0 ? null : "The unreadable data was kept as " + string.Join(" and ", kept) + ".";
        }

        private void Save()
        {
            try
            {
                string json = JsonConvert.SerializeObject(stats);
                byte[] data = System.Text.Encoding.UTF8.GetBytes(json);
                sapi.WorldManager.SaveGame.StoreData(SaveKey, data);
            }
            catch (System.Exception e)
            {
                sapi.World.Logger.Warning("LeaderboardData: failed to save. {0}", e.Message);
            }
        }
    }
}
