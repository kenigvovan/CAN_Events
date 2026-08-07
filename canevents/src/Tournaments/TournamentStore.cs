using System;
using System.Collections.Generic;
using canevents.src.Scoring;
using Vintagestory.API.Server;

namespace canevents.src.Tournaments
{
    /// <summary>
    /// Persistent home of every tournament (ModConfig/canevents/tournaments.json). A tournament holds
    /// real player property (the pot) and must survive a restart mid-bracket, so every state change
    /// goes through <see cref="Mutate"/>, which saves before releasing the lock - no dirty window.
    /// Objects in Data.Active are the authoritative live state: never hand one to network/GUI, use a DTO.
    /// </summary>
    public class TournamentStore : JsonFileStore<TournamentData>
    {
        public TournamentStore(ICoreServerAPI sapi) : base(sapi, "tournaments.json", "TournamentStore") { }

        /// <summary>Reads a value out of the store under the lock. Copy what you need; don't leak references.</summary>
        public T Read<T>(Func<TournamentData, T> read)
        {
            lock (lockObj) return read(Data);
        }

        /// <summary>Mutates the store and persists the result before the lock is released.</summary>
        public void Mutate(Action<TournamentData> mutate)
        {
            lock (lockObj)
            {
                mutate(Data);
                Save();
            }
        }

        /// <summary>
        /// Runs <paramref name="mutate"/> and saves only when it returns true. For the common
        /// "check a condition, act, report" admin operation.
        /// </summary>
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

        /// <summary>The live tournament with this id, or null. Caller must not mutate it outside Mutate().</summary>
        public Tournament FindActive(long id)
        {
            lock (lockObj)
            {
                foreach (var t in Data.Active) if (t.Id == id) return t;
                return null;
            }
        }

        /// <summary>Snapshot of the active list, safe to iterate outside the lock (the elements are live).</summary>
        public List<Tournament> ActiveSnapshot()
        {
            lock (lockObj) return new List<Tournament>(Data.Active);
        }

        /// <summary>Moves a finished/cancelled tournament into the history, trimmed to the newest <paramref name="keep"/>.</summary>
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

        /// <summary>Parks a payout for an offline player; delivered by the manager on reconnect.</summary>
        public void QueuePayout(PendingPayout payout)
        {
            if (payout == null || payout.Items == null || payout.Items.Count == 0) return;
            lock (lockObj)
            {
                Data.Pending.Add(payout);
                Save();
            }
        }

        /// <summary>Takes (and removes) everything owed to <paramref name="uid"/>.</summary>
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
