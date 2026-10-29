using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers;
using canevents.src.EventManagers.eventBehavior.Shared;
using canevents.src.EventManagers.events;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace canevents.src.Tournaments
{
    // Runs brackets on top of the normal match flow via StartMatchFor and OnMatchFinished/OnMatchAborted.
    // Those events only write to the store; matches start only on our 1 Hz tick, so CANEventManager
    // is never re-entered from its own lifecycle.
    public partial class TournamentManager : IDisposable
    {
        private readonly ICoreServerAPI sapi;
        private readonly CANEventManager manager;
        private readonly TournamentStore store;
        private readonly EventScheduler scheduler;
        private readonly Random rng = new Random();

        private long tickListenerId = -1;

        private Log log;

        // Called players, so the scheduler and queues leave them alone. In memory only.
        private readonly HashSet<string> calledUids = new HashSet<string>();

        // Stall log throttle.
        private readonly Dictionary<int, long> lastStallLog = new Dictionary<int, long>();

        private bool schedulerPausedByUs;

        public TournamentManager(ICoreServerAPI sapi, CANEventManager manager, TournamentStore store,
            EventScheduler scheduler)
        {
            this.sapi = sapi;
            this.manager = manager;
            this.store = store;
            this.scheduler = scheduler;
            log = Log.For(sapi?.World?.Logger, "tournament");
        }

        public static long Now => UnixTime.Now;

        public void Start()
        {
            manager.OnMatchFinished += HandleMatchFinished;
            manager.OnMatchAborted += HandleMatchAborted;
            tickListenerId = sapi.Event.RegisterGameTickListener(OnTick, 1000);
        }

        public void Dispose()
        {
            manager.OnMatchFinished -= HandleMatchFinished;
            manager.OnMatchAborted -= HandleMatchAborted;
            if (tickListenerId >= 0) sapi.Event.UnregisterGameTickListener(tickListenerId);
            tickListenerId = -1;
        }

        public bool IsCalled(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return false;
            lock (calledUids) return calledUids.Contains(uid);
        }

        private void MarkCalled(Tournament t, BracketMatch m, bool called)
        {
            lock (calledUids)
            {
                // Forfeited sides aren't marked, but are always released.
                foreach (var id in called ? m.ActiveSides : m.FilledSlots)
                {
                    var e = t.FindEntrant(id);
                    if (e?.Uids == null) continue;
                    foreach (var uid in e.Uids)
                    {
                        if (called) calledUids.Add(uid);
                        else calledUids.Remove(uid);
                    }
                }
            }
        }

        // 0 if there isn't exactly one.
        public long SoleActiveId()
        {
            var active = store.ActiveSnapshot()
                .Where(t => t.State != TournamentState.Finished && t.State != TournamentState.Cancelled)
                .ToList();
            return active.Count == 1 ? active[0].Id : 0;
        }

        // Runs under the store lock.
        private (bool ok, string message) Transition(long id, System.Func<Tournament, (bool, string)> op)
        {
            var result = store.MutateChecked(d =>
            {
                var target = d.Active.FirstOrDefault(x => x.Id == id);
                if (target == null) return (false, "no tournament #" + id);
                return op(target);
            });
            if (result.ok) PushUpdate(id);
            return result;
        }

        internal void PushUpdate(long id) => manager.NetworkHandler?.BroadcastTournamentUpdate(id);

        internal string NameOf(string uid) => RunningEvent.NameOf(sapi, uid);

        private void ResumeSchedulerIfIdle()
        {
            if (!schedulerPausedByUs || scheduler == null) return;
            foreach (var t in store.ActiveSnapshot())
                if (t.State == TournamentState.Running && t.PauseScheduler) return;
            scheduler.SetEnabled(true);
            schedulerPausedByUs = false;
        }
    }
}
