using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace canevents.src.Network
{
    // Server-side admin control: the panel's actions and the scheduler state it displays.
    public partial class ServerNetworkHandler
    {
        private EventScheduler Scheduler => sapi.ModLoader.GetModSystem<canevents>()?.eventScheduler;

        private void OnAdminAction(IServerPlayer player, AdminActionMsg msg)
        {
            if (!RequireAdmin(player)) return;

            switch (msg.Action)
            {
                case AdminAction.AnnounceEvent:
                    AnnounceEvent(player, msg.EventCode);
                    break;

                case AdminAction.StopAll:
                {
                    var toStop = eventManager.CurrentlyRunningEvents.ToList();
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
                    StartBotMatch(player, msg.EventCode, msg.IntValue);
                    break;

                case AdminAction.ScheduleInterval:
                {
                    var scheduler = Scheduler;
                    if (scheduler == null) { Reply(player, false, "Scheduler not initialized."); return; }
                    int minutes = msg.IntValue < 1 ? 1 : msg.IntValue;
                    scheduler.SetInterval(minutes);
                    Reply(player, true, "Schedule interval set to " + minutes + " min.");
                    SendScheduleStateTo(player);
                    break;
                }
            }
        }

        /// <summary>Testing shortcut: puts the admin into a match of this event against bots right
        /// now, since a one-player lobby ends instantly in most modes.</summary>
        private void StartBotMatch(IServerPlayer player, string eventCode, int botCount)
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

            int bots = Math.Clamp(botCount, 1, 32);
            var teams = new List<IReadOnlyList<string>> { new List<string> { player.PlayerUID } };
            for (int i = 0; i < bots; i++)
                teams.Add(new List<string> { Bots.BotIdentity.NewUid(i) });

            var re = eventManager.StartMatchFor(ev, teams, null, null, out string reason);
            if (re != null) re.SuppressRating = true;
            Reply(player, re != null, re != null
                ? "Started " + ev.Code + " with " + bots + " bot(s)."
                : "Could not start: " + reason);
        }

        private void AnnounceEvent(IServerPlayer player, string eventCode)
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

            serverChannel.SendPacket(new ScheduleStateMsg
            {
                Enabled = scheduler.Enabled,
                IntervalMinutes = scheduler.IntervalMinutes,
                SecondsUntilNext = scheduler.SecondsUntilNext,
                NextEventCode = scheduler.PeekNextEnabledCode(),
                RotationCodes = scheduler.RotationCodes,
                MinPlayersToStart = scheduler.MinPlayersToStart,
                MaxConcurrentEvents = scheduler.MaxConcurrentEvents,
                OnlinePlayers = sapi.World.AllOnlinePlayers.Length,
                RunningEvents = eventManager.CurrentlyRunningEvents.Count
            }, player);
        }

    }
}
