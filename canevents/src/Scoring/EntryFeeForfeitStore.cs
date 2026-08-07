using System.Collections.Generic;
using canevents.src.EventManagers.events;
using Vintagestory.API.Server;

namespace canevents.src.Scoring
{
    // One stake forfeited by a mid-match disconnect: the exact escrow charged at queue time, tagged
    // with the match it was committed to.
    public class ForfeitRecord
    {
        public long MatchId { get; set; }
        public List<RewardItem> Items { get; set; }
    }

    // Persistent parking for stakes forfeited by mid-match disconnects, keyed by UID (one wager at a
    // time). Needed because an offline player's escrow ModData is unreachable (PlayerByUid returns null),
    // so OnDisconnect grabs the stake while it still can; a record left behind is refunded on reconnect.
    public class EntryFeeForfeitStore : JsonFileStore<Dictionary<string, ForfeitRecord>>
    {
        public EntryFeeForfeitStore(ICoreServerAPI sapi)
            : base(sapi, "entryfeeforfeits.json", "EntryFeeForfeitStore") { }

        /// <summary>Parks a forfeited stake, overwriting any prior record for the player.</summary>
        public void Record(string uid, long matchId, List<RewardItem> items)
        {
            if (string.IsNullOrEmpty(uid) || items == null || items.Count == 0) return;
            lock (lockObj)
            {
                Data[uid] = new ForfeitRecord { MatchId = matchId, Items = items };
                Save();
            }
        }

        /// <summary>The player's parked forfeit, or null when they hold none.</summary>
        public ForfeitRecord Peek(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return null;
            lock (lockObj)
                return Data.TryGetValue(uid, out var rec) ? rec : null;
        }

        /// <summary>Drops a player's parked forfeit (realized into a pot or refunded).</summary>
        public void Remove(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return;
            lock (lockObj)
                if (Data.Remove(uid)) Save();
        }

        /// <summary>Snapshot of every parked forfeit committed to the given match.</summary>
        public List<(string uid, ForfeitRecord rec)> ForMatch(long matchId)
        {
            var result = new List<(string, ForfeitRecord)>();
            lock (lockObj)
                foreach (var kv in Data)
                    if (kv.Value.MatchId == matchId) result.Add((kv.Key, kv.Value));
            return result;
        }
    }
}
