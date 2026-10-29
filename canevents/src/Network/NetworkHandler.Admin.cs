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

        // Shared with /eventadmin: same defaults, limits and wording.
        private EventManagers.AdminActions Admin => eventManager.AdminActions;

        private void OnAdminAction(IServerPlayer player, AdminActionMsg msg)
        {
            if (!RequireAdmin(player)) return;

            (bool ok, string message) result;
            bool refreshSchedule = false;
            switch (msg.Action)
            {
                case AdminAction.ForceStart:
                    result = Admin.ForceStart(msg.EventCode);
                    refreshSchedule = result.ok;
                    break;

                // A negative length asks for the server's default window, as /eventadmin open does
                // when the minutes are left out.
                case AdminAction.OpenSignup:
                    result = Admin.OpenSignup(msg.EventCode, msg.IntValue);
                    break;

                case AdminAction.CancelSignup:
                    result = Admin.CancelSignup(msg.EventCode);
                    break;

                case AdminAction.StopAll:
                    result = Admin.StopAll();
                    break;

                case AdminAction.ScheduleOn:
                case AdminAction.ScheduleOff:
                    result = Admin.SetScheduleEnabled(msg.Action == AdminAction.ScheduleOn);
                    refreshSchedule = result.ok;
                    break;

                case AdminAction.ScheduleInterval:
                    result = Admin.SetScheduleInterval(msg.IntValue);
                    refreshSchedule = result.ok;
                    break;

                case AdminAction.RotationAdd:
                case AdminAction.RotationRemove:
                    result = Admin.SetInRotation(msg.EventCode, msg.Action == AdminAction.RotationAdd);
                    refreshSchedule = true;
                    break;

                case AdminAction.BotMatch:
                    result = Admin.StartBotMatch(player, msg.EventCode, msg.IntValue, ToRole(msg.BotMode));
                    break;

                default:
                    return;
            }

            Reply(player, result.ok, result.message);
            if (refreshSchedule) SendScheduleStateTo(player);
        }

        private static EventManagers.BotMatchRole ToRole(BotMatchMode mode) => mode switch
        {
            BotMatchMode.Watch => EventManagers.BotMatchRole.Watch,
            BotMatchMode.Unattended => EventManagers.BotMatchRole.Unattended,
            _ => EventManagers.BotMatchRole.Play
        };

        private void OnRequestScheduleState(IServerPlayer player, RequestScheduleStateMsg msg)
        {
            if (!RequireAdmin(player)) return;
            SendScheduleStateTo(player);
        }

        private void SendScheduleStateTo(IServerPlayer player)
        {
            var scheduler = Scheduler;
            if (scheduler == null) return;

            var open = Admin.OpenSignups();

            adminChannel.SendPacket(new ScheduleStateMsg
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
