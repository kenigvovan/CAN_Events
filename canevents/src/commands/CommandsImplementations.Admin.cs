using System;
using System.Linq;
using canevents.src.EventManagers;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace canevents.src.commands
{
    // /eventadmin, backed by AdminActions like the admin panel.
    public partial class CommandsImplementations
    {
        private TextCommandResult AdminCall(System.Func<EventManagers.AdminActions, (bool ok, string message)> call)
        {
            var actions = adminActions;
            if (actions == null) return TextCommandResult.Error(Lang.Get("canevents:scheduler-not-initialized"));
            return Wrap(call(actions));
        }

        public TextCommandResult AdminForceStart(TextCommandCallingArgs args)
            => AdminCall(a => a.ForceStart((string)args.Parsers[0].GetValue()));

        public TextCommandResult AdminOpenSignup(TextCommandCallingArgs args)
        {
            string eventCode = (string)args.Parsers[0].GetValue();
            // Omitted is null (server default); 0 means a window only the admin closes.
            var rawMinutes = args.Parsers[1].GetValue();
            int? minutes = rawMinutes == null ? (int?)null : Convert.ToInt32(rawMinutes);
            return AdminCall(a => a.OpenSignup(eventCode, minutes));
        }

        public TextCommandResult AdminSignupStatus(TextCommandCallingArgs args)
        {
            if (adminActions == null) return TextCommandResult.Error(Lang.Get("canevents:scheduler-not-initialized"));

            var open = adminActions.OpenSignups();
            if (open.Count == 0) return TextCommandResult.Success("No registration window is open.");

            var lines = open.Select(w =>
            {
                var ev = canEventManager.FindEventByCode(w.code);
                int registered = canEventManager.CountRegistrations(ev);
                int max = ev?.AssemblingEB?.MaxPartiesCount ?? 0;
                string signedUp = max > 0 ? registered + "/" + max : registered.ToString();
                return w.secondsLeft < 0f
                    ? string.Format("{0}: signed up {1}, starts when you run /eventadmin start {0}", w.code, signedUp)
                    : string.Format("{0}: signed up {1}, starts in {2}",
                        w.code, signedUp, TimeText.Short(w.secondsLeft));
            });
            return TextCommandResult.Success("Registration open:\n" + string.Join("\n", lines));
        }

        // No key: the list. Key without value: that one row. "Behavior.key" picks between two behaviors.
        public TextCommandResult AdminSetting(TextCommandCallingArgs args)
        {
            string code = (string)args.Parsers[0].GetValue();
            string key = args.Parsers[1].GetValue() as string;
            string value = args.Parsers[2].GetValue() as string;

            if (canEventManager.FindEventByCode(code) == null)
                return TextCommandResult.Error(Lang.Get("canevents:event-not-found", code));

            string behavior = null;
            int dot = key?.IndexOf('.') ?? -1;
            if (dot > 0) { behavior = key.Substring(0, dot); key = key.Substring(dot + 1); }

            if (value == null)
            {
                var rows = canEventManager.EventSettings(code)
                    .Where(s => key == null || (string.Equals(s.Setting.Key, key, StringComparison.OrdinalIgnoreCase)
                        && (behavior == null || string.Equals(s.Behavior, behavior, StringComparison.OrdinalIgnoreCase))))
                    .Select(s => s.Behavior + "." + s.Setting.Key + " = " + s.Value
                                 + (s.Value == s.Default ? "" : "  (" + Lang.Get("canevents:setting-default", s.Default) + ")"))
                    .ToList();
                if (rows.Count == 0) return TextCommandResult.Error(Lang.Get("canevents:setting-unknown", key ?? "", code));
                return TextCommandResult.Success(Lang.Get("canevents:setting-list", code) + "\n" + string.Join("\n", rows));
            }

            return Wrap(canEventManager.SetEventSetting(code, behavior, key, value));
        }

        public TextCommandResult AdminCancelSignup(TextCommandCallingArgs args)
            => AdminCall(a => a.CancelSignup((string)args.Parsers[0].GetValue()));

        public TextCommandResult AdminStopAll(TextCommandCallingArgs args)
            => AdminCall(a => a.StopAll());

        public TextCommandResult AdminScheduleOn(TextCommandCallingArgs args)
            => AdminCall(a => a.SetScheduleEnabled(true));

        public TextCommandResult AdminScheduleOff(TextCommandCallingArgs args)
            => AdminCall(a => a.SetScheduleEnabled(false));

        public TextCommandResult AdminScheduleInterval(TextCommandCallingArgs args)
            => AdminCall(a => a.SetScheduleInterval((int)args.Parsers[0].GetValue()));

        public TextCommandResult AdminRotationList(TextCommandCallingArgs args)
            => AdminCall(a => a.DescribeRotation());

        public TextCommandResult AdminRotationAdd(TextCommandCallingArgs args)
            => AdminCall(a => a.SetInRotation((string)args.Parsers[0].GetValue(), true));

        public TextCommandResult AdminRotationRemove(TextCommandCallingArgs args)
            => AdminCall(a => a.SetInRotation((string)args.Parsers[0].GetValue(), false));

        // See canevents.ReloadDefinitions for what gets swapped.
        public TextCommandResult AdminReload(TextCommandCallingArgs args)
        {
            var mod = sapi.ModLoader.GetModSystem<canevents>();
            if (mod?.CANEventManager == null) return TextCommandResult.Error(Lang.Get("canevents:scheduler-not-initialized"));

            string arg = (args.Parsers[0].GetValue() as string)?.Trim();
            bool force = string.Equals(arg, "force", StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(arg) && !force)
                return TextCommandResult.Error(Lang.Get("canevents:reload-usage"));

            var r = mod.ReloadDefinitions(sapi, force);

            var sb = new System.Text.StringBuilder();
            if (r.BlockedBy.Count > 0)
            {
                sb.Append(Lang.Get("canevents:reload-refused", string.Join(", ", r.BlockedBy)));
                AppendProblems(sb, r.Problems);
                return TextCommandResult.Error(ChatSafe(sb.ToString()));
            }

            sb.Append(Lang.Get("canevents:reload-done", r.ActiveEvents, r.Added.Count, r.Changed.Count,
                r.Removed.Count, r.Unchanged, r.Templates));
            AppendList(sb, "canevents:reload-added", r.Added);
            AppendList(sb, "canevents:reload-changed", r.Changed);
            AppendList(sb, "canevents:reload-removed", r.Removed);
            AppendList(sb, "canevents:reload-kept-on-error", r.KeptOnError);
            AppendList(sb, "canevents:reload-queues-dropped", r.DroppedQueues);
            AppendList(sb, "canevents:reload-templates-kept", r.KeptTemplates);
            if (r.RunningMatches > 0 && r.Changed.Count + r.Removed.Count > 0)
                sb.Append('\n').Append(Lang.Get("canevents:reload-running-kept", r.RunningMatches));
            AppendProblems(sb, r.Problems);
            return TextCommandResult.Success(ChatSafe(sb.ToString()));
        }

        private static void AppendList(System.Text.StringBuilder sb, string langKey, System.Collections.Generic.List<string> items)
        {
            if (items == null || items.Count == 0) return;
            sb.Append('\n').Append(Lang.Get(langKey, string.Join(", ", items)));
        }

        private static void AppendProblems(System.Text.StringBuilder sb, System.Collections.Generic.List<string> problems)
        {
            if (problems == null || problems.Count == 0) return;
            sb.Append('\n').Append(Lang.Get("canevents:reload-problems", problems.Count));
            foreach (var p in problems) sb.Append("\n- ").Append(p);
        }

        // Chat is VTML, so angle brackets in parser errors would be read as tags.
        private static string ChatSafe(string text) => text.Replace('<', '‹').Replace('>', '›');

        public TextCommandResult AdminBlacklistAdd(TextCommandCallingArgs args)
        {
            if (!TryResolvePlayerArg(args, 0, out var uid, out var name, out var error)) return error;

            bool changed = canEventManager.SetPlayerBlacklisted(uid, true);
            return TextCommandResult.Success(changed
                ? "Blacklisted '" + name + "' from the event queue."
                : "'" + name + "' is already blacklisted.");
        }

        public TextCommandResult AdminBlacklistRemove(TextCommandCallingArgs args)
        {
            if (!TryResolvePlayerArg(args, 0, out var uid, out var name, out var error)) return error;

            bool changed = canEventManager.SetPlayerBlacklisted(uid, false);
            return TextCommandResult.Success(changed
                ? "Removed '" + name + "' from the event-queue blacklist."
                : "'" + name + "' was not blacklisted.");
        }

        public TextCommandResult AdminBlacklistList(TextCommandCallingArgs args)
        {
            var uids = canEventManager.BlacklistedUids.ToList();
            if (uids.Count == 0) return TextCommandResult.Success("Queue blacklist is empty.");

            var names = uids.Select(uid => PlayerModDataAccess.NameOf(sapi, uid));
            return TextCommandResult.Success("Blacklisted players: " + string.Join(", ", names));
        }

        public TextCommandResult AdminRateCapShow(TextCommandCallingArgs args)
        {
            if (!TryResolvePlayerArg(args, 0, out var uid, out var name, out var error)) return error;

            var limiter = canEventManager.RewardDistributor?.RateLimiter;
            if (limiter == null) return TextCommandResult.Success("Reward rate-cap is disabled.");

            string status = limiter.DescribeAvailability(uid, UnixTime.Now);
            return TextCommandResult.Success(string.Format("'{0}': {1}", name, status));
        }

        public TextCommandResult AdminRateCapReset(TextCommandCallingArgs args)
        {
            if (!TryResolvePlayerArg(args, 0, out var uid, out var name, out var error)) return error;

            var limiter = canEventManager.RewardDistributor?.RateLimiter;
            if (limiter == null) return TextCommandResult.Success("Reward rate-cap is disabled.");

            limiter.Reset(uid);
            return TextCommandResult.Success("Reset reward rate-cap for '" + name + "'.");
        }
    }
}
