using System;
using System.Collections.Generic;
using canevents.src.Scoring;
using Vintagestory.API.Server;

namespace canevents.src.Tournaments
{
    // ModConfig/canevents/tournaments.json. Holds the pot, so every change saves inside the lock.
    public class TournamentStore : JsonFileStore<TournamentData>
    {
        public TournamentStore(ICoreServerAPI sapi) : base(sapi, "tournaments.json", "TournamentStore") { }

        public T Read<T>(Func<TournamentData, T> read)
        {
            lock (lockObj) return read(Data);
        }

        public void Mutate(Action<TournamentData> mutate)
        {
            lock (lockObj)
            {
                mutate(Data);
                Save();
            }
        }

        // Saves only when ok.
        public (bool ok, string message) MutateChecked(Func<TournamentData, (bool ok, string message)> mutate)
        {
            lock (lockObj)
            {
                var result = mutate(Data);
                if (result.ok) Save();
                return result;
            }
        }

        public long AllocateId()
        {
            lock (lockObj)
            {
                long id = Data.NextId++;
                Save();
                return id;
            }
        }

        // Live object, only modify inside Mutate().
        public Tournament FindActive(long id)
        {
            lock (lockObj)
            {
                foreach (var t in Data.Active) if (t.Id == id) return t;
                return null;
            }
        }

        // Copy of the list, elements are still live.
        public List<Tournament> ActiveSnapshot()
        {
            lock (lockObj) return new List<Tournament>(Data.Active);
        }

        public void Archive(Tournament t, int keep = 20)
        {
            lock (lockObj)
            {
                Data.Active.Remove(t);
                Data.History.Insert(0, t);
                while (Data.History.Count > keep) Data.History.RemoveAt(Data.History.Count - 1);
                Save();
            }
        }

        public void QueuePayout(PendingPayout payout)
        {
            if (payout == null || payout.Items == null || payout.Items.Count == 0) return;
            lock (lockObj)
            {
                Data.Pending.Add(payout);
                Save();
            }
        }

        public List<PendingPayout> TakePayouts(string uid)
        {
            var mine = new List<PendingPayout>();
            if (string.IsNullOrEmpty(uid)) return mine;
            lock (lockObj)
            {
                for (int i = Data.Pending.Count - 1; i >= 0; i--)
                {
                    if (Data.Pending[i].Uid != uid) continue;
                    mine.Add(Data.Pending[i]);
                    Data.Pending.RemoveAt(i);
                }
                if (mine.Count > 0) Save();
            }
            return mine;
        }
    }
}
