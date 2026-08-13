using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace canevents.src.EventManagers
{
    // Watching a match you are not playing in. An observer is a spectator seat plus a name on the
    // match's HUD feed - nothing else about the match knows they exist, which is the point: an admin
    // who wants to see the fight must not have to occupy a slot in it.
    public partial class CANEventManager
    {
        /// <summary>Live matches, newest first, for the "what can I watch" surfaces (the chat
        /// command's list and the browser's running section).</summary>
        public List<RunningEvent> WatchableEvents()
            => RunningEventsSnapshot()
                .Where(re => re.EventState != EventState.Finished)
                .OrderByDescending(re => re.Id)
                .ToList();

        /// <summary>
        /// Seats a player as an observer of the given match. <paramref name="eventId"/> of 0 means
        /// "the only one running", which is what an admin on a quiet server always wants.
        /// Returns a localized message either way, for the command and the packet handler to relay.
        ///
        /// On success the client is always told it is now observing - the broadcaster panel and the
        /// camera hotkeys key off that, so a path that skipped it (the chat command, auto-spectate)
        /// seated the player with no UI at all. <paramref name="notifyClient"/> only decides whether
        /// the message rides along: a chat caller prints it as the command result instead, and
        /// showing it twice reads as a bug.
        /// </summary>
        public (bool ok, string message) StartObserving(IServerPlayer player, long eventId,
            bool notifyClient = false)
        {
            if (player == null) return (false, Lang.Get("canevents:spectate-failed"));
            if (SpectatorManager == null) return (false, Lang.Get("canevents:spectate-failed"));

            // Playing and watching at once cannot work: the seat would strip the gear and gamemode of
            // a body the match still expects to fight.
            if (IsInLiveEvent(player.PlayerUID))
                return (false, Lang.Get("canevents:spectate-already-playing"));

            if (SpectatorManager.IsSpectating(player.PlayerUID))
                return (false, Lang.Get("canevents:spectate-already-watching"));

            RunningEvent target;
            if (eventId > 0)
            {
                target = FindRunningEvent(eventId);
                if (target == null || target.EventState == EventState.Finished)
                    return (false, Lang.Get("canevents:spectate-no-such-match", eventId));
            }
            else
            {
                var live = WatchableEvents();
                if (live.Count == 0) return (false, Lang.Get("canevents:spectate-none-running"));
                if (live.Count > 1) return (false, Lang.Get("canevents:spectate-pick-one"));
                target = live[0];
            }

            // Before an arena is picked there is nowhere to stand and nothing to see.
            if (target.Place == null)
                return (false, Lang.Get("canevents:spectate-not-placed", target.Event.Code));

            if (!SpectatorManager.MakeObserver(player, target))
                return (false, Lang.Get("canevents:spectate-failed"));

            target.Observers.Add(player.PlayerUID);

            string message = Lang.Get("canevents:spectate-started", target.Event.Code,
                target.Place?.Name ?? "?");

            // Watching and queueing at once leaves a registration nobody can fill: the assembly pass
            // skips a busy player but keeps their slot, so the event sits at "3/4" forever - and if
            // they registered a party, the whole party is stuck with them. Dropping rather than
            // refusing: someone asking to watch has already decided they are not playing this round.
            if (DropQueueRegistrationsToSpectate(player.PlayerUID, player.PlayerName))
                message += " " + Lang.Get("canevents:spectate-left-queue");

            // Tells the client it is observing - the broadcaster panel and the camera hotkeys are
            // gated on this, whichever way the player got here.
            NetworkHandler?.SendSpectateState(player, true, target.Id, notifyClient ? message : null);

            // The HUD opens on a state packet and fills in on a scoreboard packet; both are otherwise
            // only sent when the match itself has news, which can be a while.
            NetworkHandler?.SendStateUpdateTo(target, player);
            NetworkHandler?.SendScoreboardTo(target, player);

            sapi.World.Logger.Notification("{0} is now observing event {1} (id {2})",
                player.PlayerName, target.Event.Code, target.Id);

            return (true, message);
        }

        /// <summary>
        /// Takes a player out of the observer seat and off every match's feed. Safe to call for a
        /// player who is not observing (returns false) and for one who is offline.
        /// </summary>
        public bool StopObserving(IServerPlayer player, bool notifyClient = false)
        {
            if (player == null) return false;

            bool wasObserving = DropObserverFromMatches(player.PlayerUID, player);
            if (!wasObserving) return false;

            NetworkHandler?.ForgetSpectator(player.PlayerUID);
            SpectatorManager?.RemoveSpectator(player);

            // Closes the broadcaster panel on the client. Without it the panel hangs around drawing
            // a match this player no longer receives telemetry for.
            NetworkHandler?.SendSpectateState(player, false, 0,
                notifyClient ? Lang.Get("canevents:spectate-stopped") : null);

            sapi.World.Logger.Notification("{0} stopped observing", player.PlayerName);
            return true;
        }

        /// <summary>
        /// Unseats everyone watching a match that is ending. Separate from
        /// <see cref="SpectatorManager.RemoveAllSpectators"/>, which restores the gamemode but knows
        /// nothing about the network: without the state packet the client keeps thinking it observes.
        /// </summary>
        internal void ReleaseObservers(RunningEvent re)
        {
            if (re == null) return;

            foreach (var uid in re.Observers)
            {
                NetworkHandler?.ForgetSpectator(uid);
                if (sapi.World.PlayerByUid(uid) is IServerPlayer observer)
                    NetworkHandler?.SendSpectateState(observer, false, 0);
            }
            re.Observers.Clear();
            NetworkHandler?.ForgetMatchTelemetry(re.Id);
        }

        /// <summary>Removes a uid from every match's observer set. Used by the leave path and by
        /// disconnect, where there is no client left to tell.</summary>
        internal bool DropObserverFromMatches(string uid, IServerPlayer notify = null)
        {
            bool found = false;
            foreach (var re in RunningEventsSnapshot())
            {
                if (!re.Observers.Remove(uid)) continue;
                found = true;
                // The client closes its HUD when the match it is drawing reports Finished; without
                // this it would keep a live-looking HUD for a match it no longer receives.
                if (notify != null) NetworkHandler?.SendMatchClosedTo(re, notify);
            }
            return found;
        }

        /// <summary>
        /// Auto-seats admins as observers of a match that just went live, when the server is
        /// configured for it. Only admins who are idle: anyone playing, queued, already watching or
        /// mid-tournament is left alone.
        /// </summary>
        internal void AutoObserveOnStart(RunningEvent re)
        {
            if (!config.autoSpectateAdmins || re == null) return;

            foreach (var player in sapi.World.AllOnlinePlayers.OfType<IServerPlayer>().ToList())
            {
                if (!player.HasPrivilege(CanEventsPrivileges.Spectate)) continue;
                if (IsBusy(player.PlayerUID)) continue;
                if (SpectatorManager != null && SpectatorManager.IsSpectating(player.PlayerUID)) continue;
                // Waiting in a queue is not "idle": seating them would drop the very registration
                // they are waiting on. Asking to watch does that knowingly; this fires on its own.
                if (_store.SnapshotForPlayer(player.PlayerUID).Count > 0) continue;
                // A creative-mode admin building somewhere is not idle in any useful sense, and
                // dropping them into a spectator seat mid-build would be worse than not helping.
                if (player.WorldData?.CurrentGameMode == Vintagestory.API.Common.EnumGameMode.Creative) continue;

                // Only a success is worth saying: this fires on its own, and an admin who is busy
                // for some reason we did not filter on does not need a refusal they never asked for.
                var (ok, message) = StartObserving(player, re.Id);
                if (!ok) continue;

                player.SendMessage(0, message, Vintagestory.API.Common.EnumChatType.Notification);
                player.SendMessage(0, Lang.Get("canevents:spectate-auto-hint"),
                    Vintagestory.API.Common.EnumChatType.Notification);
            }
        }
    }
}
