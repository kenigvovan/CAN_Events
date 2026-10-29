using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace canevents.src.EventManagers
{
    // Ready check for queue matches. Runs before the arena search; no-shows become pre-start dropouts.
    public partial class CANEventManager
    {
        private void BeginReadyCheck(RunningEvent re)
        {
            re.EventState = EventState.AwaitingReady;
            re.ReadyDeadlineMs = sapi.World.ElapsedMilliseconds
                + Max(5, config.queueReadyCheckSeconds) * 1000L;

            foreach (var uid in re.ParticipantsBefore)
            {
                if (Bots.BotIdentity.IsBot(uid)) { re.ReadyUids.Add(uid); continue; }

                if (!(sapi.World.PlayerByUid(uid) is IServerPlayer player)) continue;

                NetworkHandler?.SendMatchReadyRequest(player, re, config.queueReadyCheckSeconds);
                player.SendMessage(0, Lang.Get("canevents:ready-prompt-chat",
                    re.Event.Code, config.queueReadyCheckSeconds), EnumChatType.Notification);
            }

            NetworkHandler?.SendStateUpdate(re);
        }

        public (bool ok, string message) SetMatchReady(IServerPlayer player, long eventId, bool ready)
        {
            var re = FindRunningEvent(eventId);
            if (re == null || re.EventState != EventState.AwaitingReady)
                return (false, Lang.Get("canevents:ready-nothing-pending"));

            if (!re.IsParticipant(player.PlayerUID))
                return (false, Lang.Get("canevents:ready-not-yours"));

            // Late "yes" after declining or leaving doesn't count.
            if (ready && re.Roster.IsPreStartDropout(player.PlayerUID))
                return (false, Lang.Get("canevents:ready-nothing-pending"));

            if (ready) re.ReadyUids.Add(player.PlayerUID);
            else
            {
                re.MarkPreStartDropout(player.PlayerUID);
                re.ReadyUids.Remove(player.PlayerUID);
            }

            NetworkHandler?.SendMatchReadyClosed(player, re.Id);

            // Don't wait for the next pass if this was the last answer.
            AttemptReadyChecks();

            return (true, Lang.Get(ready ? "canevents:ready-confirmed" : "canevents:ready-declined"));
        }

        public void AttemptReadyChecks()
        {
            long now = sapi.World.ElapsedMilliseconds;

            foreach (var re in RunningEventsSnapshot())
            {
                if (re.EventState != EventState.AwaitingReady) continue;

                bool everyoneAnswered = re.ParticipantsBefore.All(
                    uid => re.ReadyUids.Contains(uid) || re.Roster.IsPreStartDropout(uid));

                if (!everyoneAnswered && now < re.ReadyDeadlineMs) continue;

                log.Safely("CloseReadyCheck for " + re.Event.Code, () => CloseReadyCheck(re));
            }
        }

        private void CloseReadyCheck(RunningEvent re)
        {
            var noShows = re.ParticipantsBefore
                .Where(uid => !re.ReadyUids.Contains(uid) && !re.Roster.IsPreStartDropout(uid))
                .ToList();

            foreach (var uid in noShows) re.MarkPreStartDropout(uid);

            foreach (var uid in re.ParticipantsBefore)
                if (sapi.World.PlayerByUid(uid) is IServerPlayer player)
                    NetworkHandler?.SendMatchReadyClosed(player, re.Id);

            re.ReadyDeadlineMs = 0;

            if (!IsLineupStillViable(re, out int sidesLeft))
            {
                var requeued = RequeueConfirmed(re);
                AbortSearchingEvent(re, "not enough players confirmed the ready check",
                    "canevents:ready-cancelled", requeued);
                return;
            }

            if (noShows.Count > 0)
            {
                foreach (var uid in noShows)
                    if (sapi.World.PlayerByUid(uid) is IServerPlayer absent)
                        absent.SendMessage(0, Lang.Get("canevents:ready-missed"), EnumChatType.Notification);

                BroadcastToParticipants(re, Lang.Get("canevents:ready-dropped-players", noShows.Count));
            }

            re.EventState = EventState.SearchingForLocation;
            NetworkHandler?.SendStateUpdate(re);

            log.Notification("Ready check for {0} closed: {1} dropped, {2} side(s) left",
                re.Event.Code, noShows.Count, sidesLeft);

            // Only now, so a signup window doesn't close on a match that gets called off.
            RaiseMatchStarted(re, MatchStartSource.Queue);
        }

        private static bool IsLineupStillViable(RunningEvent re, out int sidesLeft)
        {
            sidesLeft = 0;
            foreach (var group in re.ParticipantsByGroupsBefore)
                if (group.Any(uid => !re.Roster.IsPreStartDropout(uid))) sidesLeft++;

            int minSides = re.Event?.AssemblingEB?.MinPartiesCount ?? 2;
            return sidesLeft >= System.Math.Max(2, minSides);
        }

        // Re-adds the original registrations minus no-shows, not via AddToQueue: that would refuse
        // over the held escrow and re-add the whole party. Escrow stays held.
        private HashSet<string> RequeueConfirmed(RunningEvent re)
        {
            var requeued = new HashSet<string>();
            if (!IsEventEnabled(re.Event.Code)) return requeued;
            // Current definition, re.Event may be stale after /eventadmin reload.
            var target = FindEventByCode(re.Event.Code);
            if (target == null) return requeued;
            List<Registration> originals;
            lock (runningEvents) queueRegistrationsByMatch.TryGetValue(re.Id, out originals);
            if (originals == null) return requeued;

            foreach (var original in originals)
            {
                var stay = original.PlayersGuids
                    .Where(uid => !Bots.BotIdentity.IsBot(uid)
                        && re.ReadyUids.Contains(uid)
                        && !IsPlayerBlacklisted(uid)
                        && sapi.World.PlayerByUid(uid) is IServerPlayer)
                    .ToList();
                if (stay.Count == 0) continue;

                var reg = new Registration(target, stay)
                {
                    // Keep their place in line.
                    TimeStampCreated = original.TimeStampCreated,
                    ChosenLoadoutSet = original.ChosenLoadoutSet,
                    RegistrantUid = stay.Contains(original.RegistrantUid) ? original.RegistrantUid : stay[0]
                };
                foreach (var kv in original.PerPlayerLoadoutChoices)
                    if (stay.Contains(kv.Key)) reg.PerPlayerLoadoutChoices[kv.Key] = kv.Value;

                // Shrunken party may not fit a full-party event anymore.
                if (QueueManagers.TryGetValue(target.Code, out var qm)
                    && !qm.CheckRegistrationIsSuitable(reg).ok) continue;
                if (stay.Any(uid => _store.HasPlayerRegistration(uid, target))) continue;
                if (!AddRegistration(reg)) continue;

                foreach (var uid in stay)
                {
                    requeued.Add(uid);
                    (sapi.World.PlayerByUid(uid) as IServerPlayer)?.SendMessage(0,
                        Lang.Get("canevents:ready-requeued"), EnumChatType.Notification);
                }
            }
            return requeued;
        }

        private static int Max(int a, int b) => a > b ? a : b;
    }
}
