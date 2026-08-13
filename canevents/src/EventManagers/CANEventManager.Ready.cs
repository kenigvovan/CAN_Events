using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace canevents.src.EventManagers
{
    /// <summary>
    /// "Your match is ready - are you?" for queue matches.
    ///
    /// A queue slot is taken minutes before the match assembles, and people wander off. Without this,
    /// the match simply started with them: their side fought a man down and they collected a loss
    /// they never played. Tournaments have had a ready-check for exactly this reason
    /// (<see cref="Tournaments.TournamentManager"/>); the queue did not.
    ///
    /// The window runs BEFORE the arena search, so a match nobody confirms never occupies an arena,
    /// and dropouts are removed while the roster is still the starting line-up - which is what
    /// <see cref="MatchRoster.MarkPreStartDropout"/> was already built to handle.
    /// </summary>
    public partial class CANEventManager
    {
        /// <summary>Opens the ready window and asks everyone who can answer.</summary>
        private void BeginReadyCheck(RunningEvent re)
        {
            re.EventState = EventState.AwaitingReady;
            re.ReadyDeadlineMs = sapi.World.ElapsedMilliseconds
                + Max(5, config.queueReadyCheckSeconds) * 1000L;

            foreach (var uid in re.ParticipantsBefore)
            {
                // Bots are always ready, and an offline player is answered for by the deadline.
                if (Bots.BotIdentity.IsBot(uid)) { re.ReadyUids.Add(uid); continue; }

                if (!(sapi.World.PlayerByUid(uid) is IServerPlayer player)) continue;

                NetworkHandler?.SendMatchReadyRequest(player, re, config.queueReadyCheckSeconds);
                player.SendMessage(0, Lang.Get("canevents:ready-prompt-chat",
                    re.Event.Code, config.queueReadyCheckSeconds), EnumChatType.Notification);
            }

            NetworkHandler?.SendStateUpdate(re);
        }

        /// <summary>
        /// A player's answer. Declining is immediate rather than "wait for the deadline": someone who
        /// says no has told us everything we need, and the rest should not wait out the clock for it.
        /// </summary>
        public (bool ok, string message) SetMatchReady(IServerPlayer player, long eventId, bool ready)
        {
            var re = FindRunningEvent(eventId);
            if (re == null || re.EventState != EventState.AwaitingReady)
                return (false, Lang.Get("canevents:ready-nothing-pending"));

            if (!re.IsParticipant(player.PlayerUID))
                return (false, Lang.Get("canevents:ready-not-yours"));

            if (ready) re.ReadyUids.Add(player.PlayerUID);
            else
            {
                re.MarkPreStartDropout(player.PlayerUID);
                re.ReadyUids.Remove(player.PlayerUID);
            }

            NetworkHandler?.SendMatchReadyClosed(player, re.Id);

            // Settled the moment the last answer lands, rather than on the next matchmaking pass: a
            // group that all clicked at once should not sit staring at nothing for another interval.
            AttemptReadyChecks();

            return (true, Lang.Get(ready ? "canevents:ready-confirmed" : "canevents:ready-declined"));
        }

        /// <summary>
        /// Ticked by the manager: closes windows that are full or out of time. Called from the same
        /// listener as the location search, so a match moves on the moment it is settled rather than
        /// waiting for the next matchmaking pass.
        /// </summary>
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

        // Drops whoever never answered, then either lets the match go looking for an arena or calls
        // it off.
        private void CloseReadyCheck(RunningEvent re)
        {
            var noShows = re.ParticipantsBefore
                .Where(uid => !re.ReadyUids.Contains(uid) && !re.Roster.IsPreStartDropout(uid))
                .ToList();

            foreach (var uid in noShows) re.MarkPreStartDropout(uid);

            // Everyone still in gets their prompt taken down, answered or not.
            foreach (var uid in re.ParticipantsBefore)
                if (sapi.World.PlayerByUid(uid) is IServerPlayer player)
                    NetworkHandler?.SendMatchReadyClosed(player, re.Id);

            re.ReadyDeadlineMs = 0;

            if (!IsLineupStillViable(re, out int sidesLeft))
            {
                // Everyone who DID confirm is put back in the queue: they did nothing wrong, and
                // making them re-register punishes them for someone else's absence.
                RequeueConfirmed(re);
                AbortSearchingEvent(re, "not enough players confirmed the ready check");
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
        }

        /// <summary>
        /// Can this line-up still play? Counts the sides that have anyone left, against the mode's
        /// own minimum - a 5v5 whose second team evaporated is not a match, it is one team standing
        /// in an arena.
        /// </summary>
        private static bool IsLineupStillViable(RunningEvent re, out int sidesLeft)
        {
            sidesLeft = 0;
            foreach (var group in re.ParticipantsByGroupsBefore)
                if (group.Any(uid => !re.Roster.IsPreStartDropout(uid))) sidesLeft++;

            int minSides = re.Event?.AssemblingEB?.MinPartiesCount ?? 2;
            return sidesLeft >= System.Math.Max(2, minSides);
        }

        // Puts the players who confirmed back where they were, so a cancelled match costs them only
        // the wait. Their entry-fee escrow is untouched - they are still queued for the same event.
        private void RequeueConfirmed(RunningEvent re)
        {
            foreach (var uid in re.ReadyUids)
            {
                if (Bots.BotIdentity.IsBot(uid)) continue;
                if (!(sapi.World.PlayerByUid(uid) is IServerPlayer player)) continue;

                var (ok, _) = Queueing.Join(player, re.Event.Code);
                player.SendMessage(0,
                    Lang.Get(ok ? "canevents:ready-requeued" : "canevents:ready-cancelled"),
                    EnumChatType.Notification);
            }
        }

        private static int Max(int a, int b) => a > b ? a : b;
    }
}
