using System.Collections.Generic;
using canevents.src.EventManagers.events;
using Vintagestory.API.Server;

namespace canevents.src.Scoring
{
    public class ForfeitRecord
    {
        public long MatchId { get; set; }
        public List<RewardItem> Items { get; set; }

        // Null on old records.
        public string EventCode { get; set; }

        // Match ids only persist on world save and can repeat after a crash.
        public long RecordedUnix { get; set; }
    }

    // Stakes of mid-match disconnects, by uid. Offline players' ModData is unreachable, so we grab
    // the stake on disconnect.
    public class EntryFeeForfeitStore : JsonFileStore<Dictionary<string, ForfeitRecord>>
    {
        private readonly long sessionStartUnix = UnixTime.Now;

        public EntryFeeForfeitStore(ICoreServerAPI sapi)
            : base(sapi, "entryfeeforfeits.json", "EntryFeeForfeitStore") { }

        public void Record(string uid, long matchId, List<RewardItem> items, string eventCode = null)
        {
            if (string.IsNullOrEmpty(uid) || items == null || items.Count == 0) return;
            lock (lockObj)
            {
                Data[uid] = new ForfeitRecord
                {
                    MatchId = matchId,
                    Items = items,
                    EventCode = eventCode,
                    RecordedUnix = UnixTime.Now
                };
                Save();
            }
        }

        public bool IsFromThisRun(ForfeitRecord rec) => rec != null && rec.RecordedUnix >= sessionStartUnix;

        public ForfeitRecord Peek(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return null;
            lock (lockObj)
                return Data.TryGetValue(uid, out var rec) ? rec : null;
        }

        public void Remove(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return;
            lock (lockObj)
                if (Data.Remove(uid)) Save();
        }

        // Pre-crash records with a reused id are skipped; they get refunded on reconnect.
        public List<(string uid, ForfeitRecord rec)> ForMatch(long matchId, string eventCode)
        {
            var result = new List<(string, ForfeitRecord)>();
            lock (lockObj)
                foreach (var kv in Data)
                {
                    var rec = kv.Value;
                    if (rec == null || rec.MatchId != matchId || !IsFromThisRun(rec)) continue;
                    if (rec.EventCode != null && eventCode != null
                        && !string.Equals(rec.EventCode, eventCode, System.StringComparison.OrdinalIgnoreCase)) continue;
                    result.Add((kv.Key, rec));
                }
            return result;
        }
    }
}
