using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Server;

namespace canevents.src.Scoring
{
    public class HeadToHeadRecord
    {
        /// <summary>Wins of the pair's lexicographically-lesser uid ("A" in the key).</summary>
        public int WinsA { get; set; }
        public int WinsB { get; set; }
        public int Draws { get; set; }
        public long LastPlayedUnix { get; set; }
    }

    /// <summary>
    /// Lifetime score between two players who met on opposite sides, keyed by uid pair. Kept apart
    /// from <see cref="MatchHistory"/> on purpose: that log is capped at 50 entries and stores names,
    /// so "you are 7-3 against this player" would silently forget everything older than a week.
    /// Persisted across worlds (ModConfig/canevents/headtohead.json), same as the history.
    /// </summary>
    public class HeadToHeadStore : JsonFileStore<Dictionary<string, HeadToHeadRecord>>
    {
        // Pairs, not matches: even a busy server accumulates these slowly, and pruning by last
        // meeting keeps the file bounded without anyone's active rivalry ever being the one dropped.
        private const int MaxPairs = 5000;

        public HeadToHeadStore(ICoreServerAPI sapi) : base(sapi, "headtohead.json", "HeadToHead") { }

        public void RecordWin(string winnerUid, string loserUid)
            => Bump(winnerUid, loserUid, draw: false);

        public void RecordDraw(string uidA, string uidB)
            => Bump(uidA, uidB, draw: true);

        /// <summary>This player's score against another, from their own point of view.</summary>
        public (int wins, int losses, int draws) ScoreFor(string uid, string vsUid)
        {
            lock (lockObj)
            {
                if (!Data.TryGetValue(Key(uid, vsUid, out bool firstIsA), out var rec))
                    return (0, 0, 0);

                return firstIsA
                    ? (rec.WinsA, rec.WinsB, rec.Draws)
                    : (rec.WinsB, rec.WinsA, rec.Draws);
            }
        }

        private void Bump(string firstUid, string secondUid, bool draw)
        {
            if (string.IsNullOrEmpty(firstUid) || string.IsNullOrEmpty(secondUid)
                || firstUid == secondUid) return;

            lock (lockObj)
            {
                string key = Key(firstUid, secondUid, out bool firstIsA);
                if (!Data.TryGetValue(key, out var rec))
                    Data[key] = rec = new HeadToHeadRecord();

                if (draw) rec.Draws++;
                else if (firstIsA) rec.WinsA++;
                else rec.WinsB++;

                rec.LastPlayedUnix = UnixTime.Now;

                if (Data.Count > MaxPairs)
                    foreach (var stale in Data.OrderBy(kv => kv.Value.LastPlayedUnix)
                                              .Take(Data.Count - MaxPairs).Select(kv => kv.Key).ToList())
                        Data.Remove(stale);

                Save();
            }
        }

        // One key per pair regardless of who is named first.
        private static string Key(string uid, string otherUid, out bool uidIsA)
        {
            uidIsA = string.CompareOrdinal(uid, otherUid) < 0;
            return uidIsA ? uid + "|" + otherUid : otherUid + "|" + uid;
        }
    }
}
