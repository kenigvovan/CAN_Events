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
        // Own tally: lumping ties into Losses (as the old code did) deflated everyone's win-rate.
        public int Draws { get; set; }
        public int Kills { get; set; }
        public int Deaths { get; set; }
        public int EventsPlayed { get; set; }
        // Elo rating, per (player, eventCode). The initializer doubles as the migration default for
        // pre-Elo entries. Not a tally - Add() deliberately does NOT sum it.
        public int Rating { get; set; } = EloCalculator.DefaultRating;

        [JsonIgnore]
        public float KDRatio => Deaths > 0 ? (float)Kills / Deaths : Kills;

        [JsonIgnore]
        public float WinRate => EventsPlayed > 0 ? (float)Wins / EventsPlayed * 100f : 0f;

        /// <summary>Accumulate another record's tallies into this one.</summary>
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

        /// <summary>
        /// Final flush + detach. The ServerRunPhase(Shutdown) callback cannot be unregistered, so the
        /// disposed flag no-ops it: a stale instance surviving a hot-reload must not overwrite live data.
        /// </summary>
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

        /// <summary>Records a drawn match: counts as played, but neither a win nor a loss.</summary>
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

        /// <summary>
        /// Applies an Elo update for an M-side match. sides[i] are side i's starting-roster UIDs
        /// (disconnected players still counted), ranks[i] its placement (lower = better, equal = tie).
        /// </summary>
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

        /// <summary>
        /// Current Elo rating, defaulting to 1000 for an unseen (player, event) pair. Used by ranked
        /// matchmaking to pair close ratings.
        /// </summary>
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

        /// <summary>Aggregated stats per player; null eventCode = all events combined.</summary>
        public Dictionary<string, PlayerStats> GetAllStats(string eventCode = null)
        {
            var result = new Dictionary<string, PlayerStats>();
            foreach (var kvp in stats)
            {
                var playerUid = kvp.Key;
                var aggregated = new PlayerStats();

                foreach (var evKvp in kvp.Value)
                {
                    // Case-insensitive: the filter comes from the client's browser tab, the key from the
                    // event config - same compare rule as FindEventByCode.
                    if (!string.IsNullOrEmpty(eventCode)
                        && !string.Equals(evKvp.Key, eventCode, System.StringComparison.OrdinalIgnoreCase)) continue;
                    aggregated.Add(evKvp.Value);
                    // Rating isn't a tally: carried through for a single-event view, left at the default
                    // for the combined view (a summed rating is meaningless).
                    if (!string.IsNullOrEmpty(eventCode)) aggregated.Rating = evKvp.Value.Rating;
                }

                if (aggregated.EventsPlayed > 0 || aggregated.Kills > 0 || aggregated.Deaths > 0)
                {
                    result[playerUid] = aggregated;
                }
            }
            return result;
        }

        /// <summary>
        /// One player's per-event-code stats (live records, not aggregated). Used by /canstats to show
        /// per-event MMR, which is meaningless when combined.
        /// </summary>
        public IReadOnlyDictionary<string, PlayerStats> GetPlayerStatsByEvent(string playerUid)
        {
            if (playerUid != null && stats.TryGetValue(playerUid, out var perEvent))
                return perEvent;
            return new Dictionary<string, PlayerStats>();
        }

        /// <summary>All event codes that have any stats recorded.</summary>
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
            try
            {
                byte[] data = sapi.WorldManager.SaveGame.GetData(SaveKey);
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
                // A corrupt blob must not crash world load - start empty and overwrite on next save.
                sapi.World.Logger.Warning("LeaderboardData: failed to load, starting empty. {0}", e.Message);
                stats = new();
            }
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
