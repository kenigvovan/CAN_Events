using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Server;

namespace canevents.src.Scoring
{
    public class MatchHistoryEntry
    {
        public string EventCode { get; set; }
        public long TimestampUnix { get; set; }
        public string[] WinnerNames { get; set; }
        public List<MatchPlayerScore> Scores { get; set; } = new();
        public int DurationSeconds { get; set; }
    }

    public class MatchPlayerScore
    {
        public string PlayerName { get; set; }
        public int Kills { get; set; }
        public int Deaths { get; set; }
        public bool Won { get; set; }
    }

    // Append-only log of completed matches, newest first, capped at MaxEntries. Persisted across worlds
    // (ModConfig/canevents/matchhistory.json), not in the save game.
    public class MatchHistory : JsonFileStore<List<MatchHistoryEntry>>
    {
        private const int MaxEntries = 50;

        public MatchHistory(ICoreServerAPI sapi) : base(sapi, "matchhistory.json", "MatchHistory") { }

        public IReadOnlyList<MatchHistoryEntry> Entries
        {
            get { lock (lockObj) return Data.ToList(); }
        }

        public void Add(MatchHistoryEntry entry)
        {
            lock (lockObj)
            {
                Data.Insert(0, entry);
                if (Data.Count > MaxEntries)
                    Data.RemoveRange(MaxEntries, Data.Count - MaxEntries);
                Save();
            }
        }
    }
}
