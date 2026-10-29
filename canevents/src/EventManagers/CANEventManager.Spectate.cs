using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace canevents.src.EventManagers
{
    // Observers: spectator seat + HUD feed, not on the roster.
    public partial class CANEventManager
    {
        // Newest first.
        public List<RunningEvent> WatchableEvents()
            => RunningEventsSnapshot()
                .Where(re => re.EventState != EventState.Finished)
                .OrderByDescending(re => re.Id)
                .ToList();

        // eventId 0 = the only running match. notifyClient only controls whether the message is sent
        // with the state packet; chat callers print it themselves.
        public (bool ok, string message) StartObserving(IServerPlayer player, long eventId,
            bool notifyClient = false)
        {
            if (player == null) return (false, Lang.Get("canevents:spectate-failed"));
            if (SpectatorManager == null) return (false, Lang.Get("canevents:spectate-failed"));

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

            if (target.Place == null)
                return (false, Lang.Get("canevents:spectate-not-placed", target.Event.Code));

            if (!SpectatorManager.MakeObserver(player, target))
                return (false, Lang.Get("canevents:spectate-failed"));

            target.Observers.Add(player.PlayerUID);

            string message = Lang.Get("canevents:spectate-started", target.Event.Code,
                target.Place?.Name ?? "?");

            // Assembly skips busy players but keeps their slot, so the queue would stall.
            if (DropQueueRegistrationsToSpectate(player.PlayerUID, player.PlayerName))
                message += " " + Lang.Get("canevents:spectate-left-queue");

            // Always sent: broadcaster panel and camera hotkeys depend on it.
            NetworkHandler?.SendSpectateState(player, true, target.Id, notifyClient ? message : null);

            // Push state and scoreboard now instead of waiting for the next match update.
            NetworkHandler?.SendStateUpdateTo(target, player);
            NetworkHandler?.SendScoreboardTo(target, player);

            sapi.World.Logger.Notification("{0} is now observing event {1} (id {2})",
                player.PlayerName, target.Event.Code, target.Id);

            return (true, message);
        }

        public bool StopObserving(IServerPlayer player, bool notifyClient = false)
        {
            if (player == null) return false;

            bool wasObserving = DropObserverFromMatches(player.PlayerUID, player);
            if (!wasObserving) return false;

            NetworkHandler?.ForgetSpectator(player.PlayerUID);
            SpectatorManager?.RemoveSpectator(player);

            // Closes the broadcaster panel.
            NetworkHandler?.SendSpectateState(player, false, 0,
                notifyClient ? Lang.Get("canevents:spectate-stopped") : null);

            sapi.World.Logger.Notification("{0} stopped observing", player.PlayerName);
            return true;
        }

        // /canspectate off: eliminated -> released, still playing -> refused, observer -> unseated, else just freed.
        public (bool ok, string message) LeaveSpectating(IServerPlayer player, bool notifyClient = false)
        {
            if (player == null) return (false, Lang.Get("canevents:spectate-not-watching"));

            (bool ok, string message) result;
            if (TryReleaseEliminated(player))
                result = (true, Lang.Get("canevents:spectator-left"));
            else if (IsInLiveEvent(player.PlayerUID))
                result = (false, Lang.Get("canevents:spectator-still-playing"));
            else if (StopObserving(player, notifyClient))
                return (true, Lang.Get("canevents:spectate-stopped"));   // sent its own state packet
            else if (SpectatorManager == null)
                result = (false, Lang.Get("canevents:spectate-failed"));
            else
            {
                // Remove anyway, recovers a seat the bookkeeping lost.
                bool seated = SpectatorManager.IsSpectating(player.PlayerUID);
                SpectatorManager.RemoveSpectator(player);
                result = seated
                    ? (true, Lang.Get("canevents:spectator-left"))
                    : (false, Lang.Get("canevents:spectate-not-watching"));
            }

            if (notifyClient) NetworkHandler?.SendSpectateState(player, false, 0, result.message);
            return result;
        }

        // RemoveAllSpectators restores the gamemode but sends no state packet; this does.
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

        internal bool DropObserverFromMatches(string uid, IServerPlayer notify = null)
        {
            bool found = false;
            foreach (var re in RunningEventsSnapshot())
            {
                if (!re.Observers.Remove(uid)) continue;
                found = true;
                // Client only closes the HUD on a Finished state.
                if (notify != null) NetworkHandler?.SendMatchClosedTo(re, notify);
            }
            return found;
        }

        // Idle admins only (config.autoSpectateAdmins).
        internal void AutoObserveOnStart(RunningEvent re)
        {
            if (!config.autoSpectateAdmins || re == null) return;

            foreach (var player in sapi.World.AllOnlinePlayers.OfType<IServerPlayer>().ToList())
            {
                if (!player.HasPrivilege(CanEventsPrivileges.Spectate)) continue;
                if (IsBusy(player.PlayerUID)) continue;
                if (SpectatorManager != null && SpectatorManager.IsSpectating(player.PlayerUID)) continue;
                // Observing would drop their queue registration.
                if (_store.SnapshotForPlayer(player.PlayerUID).Count > 0) continue;
                if (player.WorldData?.CurrentGameMode == Vintagestory.API.Common.EnumGameMode.Creative) continue;

                // Silent on failure.
                var (ok, message) = StartObserving(player, re.Id);
                if (!ok) continue;

                player.SendMessage(0, message, Vintagestory.API.Common.EnumChatType.Notification);
                player.SendMessage(0, Lang.Get("canevents:spectate-auto-hint"),
                    Vintagestory.API.Common.EnumChatType.Notification);
            }
        }
    }
}
