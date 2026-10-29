using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace canevents.src.EventManagers
{
    public enum BotMatchRole
    {
        Play,
        // Bots only; caller observes once the arena is found.
        Watch,
        // Bots only; caller can join later by id.
        Unattended
    }

    // Shared by /eventadmin and the admin panel.
    public sealed class AdminActions
    {
        private readonly ICoreServerAPI sapi;
        private readonly CANEventManager manager;

        public AdminActions(ICoreServerAPI sapi, CANEventManager manager)
        {
            this.sapi = sapi;
            this.manager = manager;
        }

        private canevents Mod => sapi.ModLoader.GetModSystem<canevents>();
        private EventScheduler Scheduler => Mod?.eventScheduler;
        private SignupWindows Signups => Mod?.signupWindows;

        public int DefaultSignupMinutes => Mod?.config?.signupWindowMinutes ?? 5;

        private static (bool, string) NotReady => (false, Lang.Get("canevents:scheduler-not-initialized"));

        public (bool ok, string message) ForceStart(string eventCode)
        {
            var scheduler = Scheduler;
            if (scheduler == null) return NotReady;
            if (string.IsNullOrWhiteSpace(eventCode)) return (false, Lang.Get("canevents:event-not-found", ""));

            return scheduler.ForceStart(eventCode, out var reason)
                ? (true, Lang.Get("canevents:force-started", eventCode))
                : (false, reason);
        }

        // minutes null or negative (panel) = config default; 0 = closed by an admin only.
        public (bool ok, string message) OpenSignup(string eventCode, int? minutes)
        {
            var signups = Signups;
            if (signups == null) return NotReady;

            int length = minutes == null || minutes.Value < 0 ? DefaultSignupMinutes : minutes.Value;
            bool ok = signups.Open(eventCode, length, out var message);
            return (ok, message);
        }

        public (bool ok, string message) CancelSignup(string eventCode)
        {
            var signups = Signups;
            if (signups == null) return NotReady;

            bool ok = signups.Cancel(eventCode, out var message);
            return (ok, message);
        }

        // secondsLeft < 0 = no deadline.
        public List<(string code, float secondsLeft)> OpenSignups()
            => Signups?.OpenWindows() ?? new List<(string code, float secondsLeft)>();

        public (bool ok, string message) StopAll()
        {
            var toStop = manager.RunningEventsSnapshot();
            // Open matches stay down until /eventadmin live start, or the keeper would bring them back.
            foreach (var re in toStop)
            {
                if (re.IsOpenMatch) manager.StopOpenMatch(re);
                else manager.FinishEvent(re);
            }
            return (true, Lang.Get("canevents:stopped-all", toStop.Count));
        }

        public (bool ok, string message) SetScheduleEnabled(bool enabled)
        {
            var scheduler = Scheduler;
            if (scheduler == null) return NotReady;

            scheduler.SetEnabled(enabled);
            return (true, Lang.Get(enabled ? "canevents:schedule-enabled" : "canevents:schedule-disabled"));
        }

        public (bool ok, string message) SetScheduleInterval(int minutes)
        {
            var scheduler = Scheduler;
            if (scheduler == null) return NotReady;

            // SetInterval clamps; report the value it actually kept.
            scheduler.SetInterval(minutes);
            return (true, Lang.Get("canevents:schedule-interval", scheduler.IntervalMinutes));
        }

        public (bool ok, string message) SetInRotation(string eventCode, bool inRotation)
        {
            var scheduler = Scheduler;
            if (scheduler == null) return NotReady;
            if (string.IsNullOrWhiteSpace(eventCode)) return (false, Lang.Get("canevents:event-not-found", ""));

            // Removing works for stale codes too.
            var ev = manager.FindEventByCode(eventCode);
            if (inRotation && ev == null) return (false, Lang.Get("canevents:event-not-found", eventCode));
            string code = ev?.Code ?? eventCode;

            bool changed = scheduler.SetInRotation(code, inRotation);
            string list = RotationText(scheduler);
            if (!changed)
                return (false, Lang.Get(inRotation ? "canevents:rotation-already-in" : "canevents:rotation-not-in", code, list));
            return (true, Lang.Get(inRotation ? "canevents:rotation-added" : "canevents:rotation-removed", code, list));
        }

        public (bool ok, string message) DescribeRotation()
        {
            var scheduler = Scheduler;
            if (scheduler == null) return NotReady;
            return (true, Lang.Get("canevents:rotation-list", RotationText(scheduler)));
        }

        private static string RotationText(EventScheduler scheduler)
            => scheduler.RotationCodes.Length == 0 ? "-" : string.Join(", ", scheduler.RotationCodes);

        // Testing: match against bots. Watch/Unattended let a lone admin test spectating.
        public (bool ok, string message) StartBotMatch(IServerPlayer caller, string eventCode, int botCount,
            BotMatchRole role)
        {
            var template = manager.FindEventByCode(eventCode);
            if (template == null) return (false, "No such event: " + eventCode);
            // Open match: it's already running, the bots walk into it; the caller walks in through a pad.
            if (CANEventManager.OpenModeOf(template) != null)
                return manager.AddBotsToOpenMatch(template.Code, botCount);

            // Clone with ArenaBots added, no rewards or fee.
            var ev = Bots.BotMatchSupport.EnsureBotsCanSpawn(template.Clone());
            ev.Reward = null;
            ev.EntryFee = null;
            ev.Rated = false;

            bool playing = role == BotMatchRole.Play;

            // StartMatchFor needs at least two teams.
            int bots = Math.Clamp(botCount, playing ? 1 : 2, 32);

            var teams = new List<IReadOnlyList<string>>();
            // Modes that pick sides in a waiting room (battle) play a fixed number of teams: the caller
            // on the first, bots dealt round the rest. Elsewhere every bot is a side of its own.
            int sides = template.GetBehavior<eventBehavior.Shared.IPreStartLobby>() != null
                ? CANEventManager.TeamCountOf(template) : 0;
            if (sides > 0)
            {
                bots = Math.Max(bots, sides - (playing ? 1 : 0));
                var dealt = Enumerable.Range(0, sides).Select(_ => new List<string>()).ToList();
                int seat = 0;
                if (playing) dealt[seat++].Add(caller.PlayerUID);
                for (int i = 0; i < bots; i++)
                    dealt[seat++ % sides].Add(Bots.BotIdentity.NewUid(i));
                teams.AddRange(dealt);
            }
            else
            {
                if (playing) teams.Add(new List<string> { caller.PlayerUID });
                for (int i = 0; i < bots; i++)
                    teams.Add(new List<string> { Bots.BotIdentity.NewUid(i) });
            }

            var re = manager.StartMatchFor(ev, teams, null, null, out string reason);
            if (re == null) return (false, "Could not start: " + reason);

            re.SuppressRating = true;

            string tail = role switch
            {
                BotMatchRole.Watch => " - taking you to the stands.",
                BotMatchRole.Unattended => " (id " + re.Id + "). Join it with /canspectate watch "
                    + re.Id + ", or from the events browser.",
                _ => "."
            };

            if (role == BotMatchRole.Watch) ObserveWhenPlaced(caller, re);
            return (true, "Started " + ev.Code + " with " + bots + " bot(s)" + tail);
        }

        // Polls until the match has an arena.
        private void ObserveWhenPlaced(IServerPlayer player, RunningEvent re)
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

                var match = manager.FindRunningEvent(matchId);
                if (match == null || match.EventState == EventState.Finished) return;

                if (match.Place != null)
                {
                    var (_, message) = manager.StartObserving(pl, matchId, notifyClient: true);
                    if (message != null) sapi.World.Logger.VerboseDebug("[canevents] bot-match observe: {0}", message);
                    return;
                }

                if (--left > 0) sapi.Event.RegisterCallback(Retry, intervalMs);
                else pl.SendMessage(0, Lang.Get("canevents:spectate-not-placed", match.Event.Code),
                    EnumChatType.Notification);
            }

            sapi.Event.RegisterCallback(Retry, intervalMs);
        }
    }
}
