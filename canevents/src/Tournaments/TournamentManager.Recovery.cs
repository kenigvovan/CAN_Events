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
    public partial class TournamentManager
    {
        public void OnReconnect(IServerPlayer player)
        {
            DeliverPendingPayouts(player);
            ResendReadyCheck(player);
        }

        // RunningEvents don't survive a restart, so in-flight matches go back to Pending.
        public void RecoverAfterRestart()
        {
            store.Mutate(d =>
            {
                foreach (var t in d.Active)
                {
                    if (t.State != TournamentState.Running) continue;
                    int reset = 0;
                    foreach (var m in t.Matches)
                    {
                        if (m.State != BracketMatchState.AwaitingReady
                            && m.State != BracketMatchState.Starting
                            && m.State != BracketMatchState.Live) continue;

                        ResetToPending(m);
                        reset++;
                    }
                    if (reset > 0)
                    {
                        log.Notification("{0}: {1} interrupted match(es) reset to pending after restart",
                            t.Id, reset);
                        Announce(Lang.Get("canevents:tournament-resumed", t.Name));
                    }
                }
            });

            lock (calledUids) calledUids.Clear();

            // Scheduler's disabled state is persisted, our pause flag isn't; rebuild it.
            foreach (var t in store.ActiveSnapshot())
            {
                if (t.State == TournamentState.Running && t.PauseScheduler
                    && scheduler != null && !scheduler.Enabled)
                {
                    schedulerPausedByUs = true;
                    break;
                }
            }
        }

        // Admin decided the match, so no Elo change.
        private void StopLiveMatch(BracketMatch m, string reason)
        {
            if (m.RunningEventId == 0) return;
            var re = manager.FindRunningEvent(m.RunningEventId);
            if (re == null) return;
            re.SuppressRating = true;
            re.RequestFinish(reason);
        }

        // A player can be in only one open tournament at a time.
        private Tournament OpenTournamentOf(string uid, long exceptId = 0)
        {
            foreach (var other in store.ActiveSnapshot())
                if (other.Id != exceptId && other.State != TournamentState.Cancelled
                    && other.State != TournamentState.Finished
                    && other.EntrantOfPlayer(uid) != null)
                    return other;
            return null;
        }

        // An emptied whitelist means "any arena", the returned line says so.
        public List<string> RemovePlaceFromWhitelists(long placeId)
        {
            var touched = new List<string>();
            var ids = new List<long>();
            store.Mutate(d =>
            {
                foreach (var t in d.Active)
                {
                    if (t.PlaceWhitelist == null || !t.PlaceWhitelist.Remove(placeId)) continue;
                    while (t.PlaceWhitelist.Remove(placeId)) { }
                    ids.Add(t.Id);
                    touched.Add("#" + t.Id + " '" + t.Name + "'" + (t.PlaceWhitelist.Count == 0
                        ? ": arena list now empty - any suitable arena will be used"
                        : ": " + t.PlaceWhitelist.Count + " arena(s) left"));
                }
            });
            foreach (var line in touched) log.Notification("arena {0} deleted, removed from {1}", placeId, line);
            foreach (var id in ids) PushUpdate(id);
            return touched;
        }
    }
}
