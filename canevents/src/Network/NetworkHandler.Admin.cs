using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace canevents.src.Network
{
    // Server-side admin control: the panel's actions and the scheduler state it displays.
    public partial class ServerNetworkHandler
    {
        private EventScheduler Scheduler => sapi.ModLoader.GetModSystem<canevents>()?.eventScheduler;
        private EventManagers.SignupWindows Signups => sapi.ModLoader.GetModSystem<canevents>()?.signupWindows;

        private void OnAdminAction(IServerPlayer player, AdminActionMsg msg)
        {
            if (!RequireAdmin(player)) return;

            switch (msg.Action)
            {
                // Named "Announce" on the wire since the first version; it has always been a
                // force-start, and the panel labels it as one.
                case AdminAction.AnnounceEvent:
                    ForceStartEvent(player, msg.EventCode);
                    break;

                case AdminAction.OpenSignup:
                {
                    var signups = Signups;
                    if (signups == null) { Reply(player, false, "Signup windows are not available."); return; }
                    bool opened = signups.Open(msg.EventCode, msg.IntValue, out var openMessage);
                    Reply(player, opened, openMessage);
                    break;
                }

                case AdminAction.CancelSignup:
                {
                    var signups = Signups;
                    if (signups == null) { Reply(player, false, "Signup windows are not available."); return; }
                    bool cancelled = signups.Cancel(msg.EventCode, out var cancelMessage);
                    Reply(player, cancelled, cancelMessage);
                    break;
                }

                case AdminAction.StopAll:
                {
                    // Snapshot: FinishEvent takes each match off the live list as it goes.
                    var toStop = eventManager.RunningEventsSnapshot();
                    foreach (var re in toStop) eventManager.FinishEvent(re);
                    Reply(player, true, "Stopped " + toStop.Count + " running event(s).");
                    break;
                }

                case AdminAction.ScheduleOn:
                    SetSchedulerEnabled(player, true);
                    break;

                case AdminAction.ScheduleOff:
                    SetSchedulerEnabled(player, false);
                    break;

                case AdminAction.BotMatch:
                    StartBotMatch(player, msg.EventCode, msg.IntValue, msg.BotMode);
                    break;

                case AdminAction.ScheduleInterval:
                {
                    var scheduler = Scheduler;
                    if (scheduler == null) { Reply(player, false, "Scheduler not initialized."); return; }
                    // No clamp here: SetInterval owns the bounds for every caller.
                    scheduler.SetInterval(msg.IntValue);
                    Reply(player, true, "Schedule interval set to " + scheduler.IntervalMinutes + " min.");
                    SendScheduleStateTo(player);
                    break;
                }
            }
        }

        /// <summary>
        /// Testing shortcut: starts a match of this event against bots right now, since a one-player
        /// lobby ends instantly in most modes. What the caller does in it is
        /// <paramref name="mode"/>: fight, be seated to watch, or nothing at all. The latter two run
        /// on bots alone, which is the only way to reach the spectator features on a server with one
        /// person on it - a participant may not watch their own match.
        /// </summary>
        private void StartBotMatch(IServerPlayer player, string eventCode, int botCount,
            BotMatchMode mode = BotMatchMode.Play)
        {
            var template = eventManager.FindEventByCode(eventCode);
            if (template == null) { Reply(player, false, "No such event: " + eventCode); return; }

            // A throwaway copy of the event: bot uids only become bodies with the ArenaBots behavior,
            // which the debug match grows for itself rather than every mode carrying it. Rewards and
            // the entry fee are dropped - a rehearsal against bots must not hand out real items.
            var ev = Bots.BotMatchSupport.EnsureBotsCanSpawn(template.Clone());
            ev.Reward = null;
            ev.EntryFee = null;
            ev.Rated = false;

            bool playing = mode == BotMatchMode.Play;

            // Two sides minimum, and with the admin out of the line-up the bots have to supply both:
            // StartMatchFor refuses a single-team match outright.
            int bots = Math.Clamp(botCount, playing ? 1 : 2, 32);

            var teams = new List<IReadOnlyList<string>>();
            if (playing) teams.Add(new List<string> { player.PlayerUID });
            for (int i = 0; i < bots; i++)
                teams.Add(new List<string> { Bots.BotIdentity.NewUid(i) });

            var re = eventManager.StartMatchFor(ev, teams, null, null, out string reason);
            if (re == null) { Reply(player, false, "Could not start: " + reason); return; }

            re.SuppressRating = true;

            string tail = mode switch
            {
                BotMatchMode.Watch => " - taking you to the stands.",
                // Says where to go next: the point of this mode is to then join the ordinary way,
                // and the match id is what /canspectate wants.
                BotMatchMode.Unattended => " (id " + re.Id + "). Join it with /canspectate watch "
                    + re.Id + ", or from the events browser.",
                _ => "."
            };
            Reply(player, true, "Started " + ev.Code + " with " + bots + " bot(s)" + tail);

            if (mode == BotMatchMode.Watch) ObserveWhenPlaced(player, re);
        }

        /// <summary>
        /// Seats the caller as an observer as soon as the match has an arena. The seat needs somewhere
        /// to hover, and a fresh match spends its first seconds in SearchingForLocation - so this
        /// waits for the arena rather than refusing on the spot.
        /// </summary>
        private void ObserveWhenPlaced(IServerPlayer player, EventManagers.RunningEvent re)
        {
            const int attempts = 20;
            const int intervalMs = 1000;

            string uid = player.PlayerUID;
            long matchId = re.Id;
            int left = attempts;

            void Retry(float _)
            {
                var pl = sapi.World.PlayerByUid(uid) as IServerPlayer;
                if (pl == null) return;

                // Gone or over before it was placed: nothing to watch, and no point saying so twice.
                var match = eventManager.FindRunningEvent(matchId);
                if (match == null || match.EventState == EventManagers.EventState.Finished) return;

                if (match.Place != null)
                {
                    var (_, message) = eventManager.StartObserving(pl, matchId, notifyClient: true);
                    if (message != null) sapi.World.Logger.VerboseDebug("[canevents] bot-match observe: {0}", message);
                    return;
                }

                if (--left > 0) sapi.Event.RegisterCallback(Retry, intervalMs);
                else pl.SendMessage(0, Lang.Get("canevents:spectate-not-placed", match.Event.Code),
                    EnumChatType.Notification);
            }

            sapi.Event.RegisterCallback(Retry, intervalMs);
        }

        private void ForceStartEvent(IServerPlayer player, string eventCode)
        {
            var scheduler = Scheduler;
            if (scheduler == null) { Reply(player, false, "Scheduler not initialized."); return; }
            if (string.IsNullOrWhiteSpace(eventCode)) { Reply(player, false, "No event code given."); return; }

            // Starts a match from the players already queued for this event; fails on an empty queue.
            bool ok = scheduler.ForceStart(eventCode, out var reason);
            Reply(player, ok, ok
                ? Lang.Get("canevents:force-started", eventCode)
                : reason);
            if (ok) SendScheduleStateTo(player);
        }

        private void SetSchedulerEnabled(IServerPlayer player, bool enabled)
        {
            var scheduler = Scheduler;
            if (scheduler == null) { Reply(player, false, "Scheduler not initialized."); return; }
            scheduler.SetEnabled(enabled);
            Reply(player, true, "Auto-schedule " + (enabled ? "enabled." : "disabled."));
            SendScheduleStateTo(player);
        }

        private void OnRequestScheduleState(IServerPlayer player, RequestScheduleStateMsg msg)
        {
            if (!RequireAdmin(player)) return;
            SendScheduleStateTo(player);
        }

        private void SendScheduleStateTo(IServerPlayer player)
        {
            var scheduler = Scheduler;
            if (scheduler == null) return;

            var open = Signups?.OpenWindows() ?? new List<(string code, float secondsLeft)>();

            serverChannel.SendPacket(new ScheduleStateMsg
            {
                OpenSignupCodes = open.Select(w => w.code).ToArray(),
                OpenSignupSeconds = open.Select(w => w.secondsLeft).ToArray(),
                Enabled = scheduler.Enabled,
                IntervalMinutes = scheduler.IntervalMinutes,
                SecondsUntilNext = scheduler.SecondsUntilNext,
                NextEventCode = scheduler.PeekNextEnabledCode(),
                RotationCodes = scheduler.RotationCodes,
                MinPlayersToStart = scheduler.MinPlayersToStart,
                MaxConcurrentEvents = scheduler.MaxConcurrentEvents,
                OnlinePlayers = sapi.World.AllOnlinePlayers.Length,
                RunningEvents = eventManager.RunningEventCount
            }, player);
        }

    }
}
