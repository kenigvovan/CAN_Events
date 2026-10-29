using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers.events;
using canevents.src.Tournaments;
using Vintagestory.API.Common;
using Vintagestory.API.Common.CommandAbbr;
using Vintagestory.API.Server;
using static canevents.src.commands.CommandArgs;

namespace canevents.src.commands
{
    public static class TournamentCommands
    {
        public static void Register(ICoreServerAPI sapi, CommandArgumentParsers parsers)
        {
            var impl = new TournamentCommandsImpl(sapi);

            sapi.ChatCommands.Create("tournament")
                .RequiresPlayer().RequiresPrivilege(Privilege.chat).WithAlias("cantour")
                    .BeginSub("list")
                        .HandleWith(impl.List)
                        .WithDesc("List the active tournaments.")
                    .EndSub()
                    .BeginSub("bracket")
                        .WithArgs(parsers.OptionalLong("tournamentId"))
                        .HandleWith(impl.Bracket)
                        .WithDesc("Show a tournament's bracket.")
                    .EndSub()
                    .BeginSub("signup")
                        .WithArgs(parsers.OptionalLong("tournamentId"))
                        .HandleWith(impl.Signup)
                        .WithDesc("Enter a tournament (pays the entry fee).")
                    .EndSub()
                    .BeginSub("team")
                        .WithArgs(parsers.Long("tournamentId"), parsers.Word("name"))
                        .HandleWith(impl.TeamName)
                        .WithDesc("Name your team in a tournament (default is 'Leader +N').")
                    .EndSub()
                    .BeginSub("class")
                        .WithArgs(parsers.Long("tournamentId"), parsers.OptionalWord("loadoutSet"))
                        .HandleWith(impl.ClassChoice)
                        .WithDesc("Pick the class you will play the whole tournament with; no name clears it.")
                    .EndSub()
                    .BeginSub("withdraw")
                        .WithArgs(parsers.OptionalLong("tournamentId"))
                        .HandleWith(impl.Withdraw)
                        .WithDesc("Leave a tournament before it starts (refunds the fee).")
                    .EndSub()
                    .BeginSub("ready")
                        .WithArgs(parsers.OptionalLong("tournamentId"))
                        .HandleWith(impl.Ready)
                        .WithDesc("Accept your called tournament match.")
                    .EndSub()
                    .BeginSub("forfeit")
                        .WithArgs(parsers.OptionalLong("tournamentId"))
                        .HandleWith(impl.Forfeit)
                        .WithDesc("Give up your called tournament match.")
                    .EndSub()

                    .BeginSub("create")
                        .RequiresPrivilege(Privilege.controlserver)
                        .WithArgs(parsers.Word("name"), parsers.Word("eventCodes"), parsers.OptionalInt("teamSize"))
                        .HandleWith(impl.Create)
                        .WithDesc("Create a tournament. eventCodes is comma-separated.")
                    .EndSub()
                    .BeginSub("arena")
                        .RequiresPrivilege(Privilege.controlserver)
                        // -1 when omitted: 0 is a real arena id.
                        .WithArgs(parsers.Long("tournamentId"), parsers.Word("action"), parsers.OptionalLong("placeId", -1))
                        .HandleWith(impl.Arena)
                        .WithDesc("add|remove|clear|list arenas the tournament may use (empty = any).")
                    .EndSub()
                    .BeginSub("fee")
                        .RequiresPrivilege(Privilege.controlserver)
                        .WithArgs(parsers.Long("tournamentId"), parsers.Word("items"))
                        .HandleWith(impl.Fee)
                        .WithDesc("Set the entry fee as code:qty;code:qty, or '-' for none.")
                    .EndSub()
                    .BeginSub("classes")
                        .RequiresPrivilege(Privilege.controlserver)
                        .WithArgs(parsers.Long("tournamentId"), parsers.Word("names"))
                        .HandleWith(impl.AllowedClasses)
                        .WithDesc("Classes allowed in this tournament, comma-separated; '-' allows all.")
                    .EndSub()
                    .BeginSub("split")
                        .RequiresPrivilege(Privilege.controlserver)
                        .WithArgs(parsers.Long("tournamentId"), parsers.Word("percents"))
                        .HandleWith(impl.Split)
                        .WithDesc("Pot split by place, e.g. 60,30,10.")
                    .EndSub()
                    .BeginSub("prize")
                        .RequiresPrivilege(Privilege.controlserver)
                        .WithArgs(parsers.Long("tournamentId"), parsers.Int("place"), parsers.Word("items"))
                        .HandleWith(impl.Prize)
                        .WithDesc("Item prize for a place, as code:qty;code:qty, or '-' to clear.")
                    .EndSub()
                    .BeginSub("set")
                        .RequiresPrivilege(Privilege.controlserver)
                        .WithArgs(parsers.Long("tournamentId"), parsers.Word("option"), parsers.Word("value"))
                        .HandleWith(impl.Set)
                        // Keep in sync with the default case in Set.
                        .WithDesc("readycheck|maxentrants|concurrent|thirdplace|seed|rated|rake|sides|advance"
                            + "|manual|owngear|lockclass|autostart|autostartdelay|scheduler|deadline"
                            + "|openin|signupwindow|repeat|repeatat|announce [value]")
                    .EndSub()
                    .BeginSub("open")
                        .RequiresPrivilege(Privilege.controlserver)
                        .WithArgs(parsers.Long("tournamentId"))
                        .HandleWith(impl.Open)
                        .WithDesc("Open signup.")
                    .EndSub()
                    .BeginSub("close")
                        .RequiresPrivilege(Privilege.controlserver)
                        .WithArgs(parsers.Long("tournamentId"))
                        .HandleWith(impl.Close)
                        .WithDesc("Close signup.")
                    .EndSub()
                    .BeginSub("begin")
                        .RequiresPrivilege(Privilege.controlserver)
                        .WithArgs(parsers.Long("tournamentId"))
                        .HandleWith(impl.Begin)
                        .WithDesc("Seed the field and start calling matches.")
                    .EndSub()
                    .BeginSub("cancel")
                        .RequiresPrivilege(Privilege.controlserver)
                        .WithArgs(parsers.Long("tournamentId"), parsers.OptionalWord("refund"))
                        .HandleWith(impl.Cancel)
                        .WithDesc("Cancel a tournament; pass 'refund' to hand the stakes back.")
                    .EndSub()
                    .BeginSub("delete")
                        .RequiresPrivilege(Privilege.controlserver)
                        .WithArgs(parsers.Long("tournamentId"))
                        .HandleWith(impl.Delete)
                        .WithDesc("Remove a tournament entirely (stakes are refunded first).")
                    .EndSub()
                    .BeginSub("result")
                        .RequiresPrivilege(Privilege.controlserver)
                        .WithArgs(parsers.Long("tournamentId"), parsers.Int("matchId"), parsers.Word("winner"))
                        .HandleWith(impl.Result)
                        .WithDesc("Declare a match winner: A, B, a seed, or a player name.")
                    .EndSub()
                    .BeginSub("next")
                        .RequiresPrivilege(Privilege.controlserver)
                        .WithArgs(parsers.OptionalLong("tournamentId"))
                        .HandleWith(impl.CallNext)
                        .WithDesc("Call the next match now - the only way matches start in manual mode.")
                    .EndSub()
                    .BeginSub("bots")
                        .RequiresPrivilege(Privilege.controlserver)
                        .WithArgs(parsers.Long("tournamentId"), parsers.Int("count"))
                        .HandleWith(impl.AddBots)
                        .WithDesc("Testing: fill the field with bot entrants (no fee, no prizes, no Elo).")
                    .EndSub()
                    .BeginSub("dq")
                        .RequiresPrivilege(Privilege.controlserver)
                        .WithArgs(parsers.Long("tournamentId"), parsers.Word("entrant"))
                        .HandleWith(impl.Disqualify)
                        .WithDesc("Disqualify an entrant.")
                    .EndSub();
        }
    }

    public class TournamentCommandsImpl
    {
        private readonly ICoreServerAPI sapi;
        public TournamentCommandsImpl(ICoreServerAPI sapi) { this.sapi = sapi; }

        private TournamentManager Tournaments
            => sapi.ModLoader.GetModSystem<canevents>()?.CANEventManager?.Tournaments;

        private EventPlacesManagers.DefaultEventPlacesManager Places
            => sapi.ModLoader.GetModSystem<canevents>()?.CANEventManager?.eventPlacesManager;

        public TextCommandResult List(TextCommandCallingArgs args)
            => Lines(Tournaments?.Describe());

        public TextCommandResult Bracket(TextCommandCallingArgs args)
        {
            var t = Tournaments;
            if (t == null) return Report(Unavailable);
            long id = ResolveId(args, 0);
            if (id <= 0) return TextCommandResult.Error("which tournament? see /tournament list");
            return Lines(t.DescribeBracket(id));
        }

        public TextCommandResult Signup(TextCommandCallingArgs args)
            => Run(args, (t, id, player) => t.Signup(player, id));

        public TextCommandResult Withdraw(TextCommandCallingArgs args)
            => Run(args, (t, id, player) => t.Withdraw(player, id));

        public TextCommandResult TeamName(TextCommandCallingArgs args)
        {
            var t = Tournaments;
            if (t == null) return Report(Unavailable);
            if (!(args.Caller.Player is IServerPlayer player)) return TextCommandResult.Error("players only");
            return Report(t.SetTeamName(player, Id(args, 0), Str(args, 1)));
        }

        public TextCommandResult ClassChoice(TextCommandCallingArgs args)
        {
            var t = Tournaments;
            if (t == null) return Report(Unavailable);
            if (!(args.Caller.Player is IServerPlayer player)) return TextCommandResult.Error("players only");
            return Report(t.SetLoadout(player, Id(args, 0), Str(args, 1)));
        }

        public TextCommandResult Delete(TextCommandCallingArgs args)
            => Report(Tournaments?.Delete(Id(args, 0)) ?? Unavailable);

        public TextCommandResult Ready(TextCommandCallingArgs args)
            => Run(args, (t, id, player) => t.SetReady(player, id, 0, true));

        public TextCommandResult Forfeit(TextCommandCallingArgs args)
            => Run(args, (t, id, player) => t.SetReady(player, id, 0, false));

        public TextCommandResult Create(TextCommandCallingArgs args)
        {
            var t = Tournaments;
            if (t == null) return Report(Unavailable);
            string name = Str(args, 0);
            var codes = (Str(args, 1) ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            int teamSize = Math.Max(1, Num(args, 2));
            return Report(t.Create(name, codes, teamSize));
        }

        public TextCommandResult Arena(TextCommandCallingArgs args)
            => Configure(args, (tour, cfg) =>
            {
                string action = (Str(args, 1) ?? "").ToLowerInvariant();
                long placeId = Id(args, 2);
                switch (action)
                {
                    case "add":
                        if (placeId < 0) return (false, "which arena?");
                        if (Places?.FindById(placeId) == null) return (false, "no arena #" + placeId + " - see /canep list");
                        if (!cfg.PlaceWhitelist.Contains(placeId)) cfg.PlaceWhitelist.Add(placeId);
                        return (true, "arenas: " + Describe(cfg.PlaceWhitelist));
                    case "remove":
                        // No existence check, so stale ids of deleted arenas can be removed.
                        if (placeId < 0) return (false, "which arena?");
                        cfg.PlaceWhitelist.Remove(placeId);
                        return (true, "arenas: " + Describe(cfg.PlaceWhitelist));
                    case "clear":
                        cfg.PlaceWhitelist.Clear();
                        return (true, "arena whitelist cleared (any suitable arena)");
                    case "list":
                        return (true, "arenas: " + Describe(cfg.PlaceWhitelist));
                    default:
                        return (false, "add|remove|clear|list");
                }
            });

        public TextCommandResult Fee(TextCommandCallingArgs args)
            => Configure(args, (tour, cfg) =>
            {
                string packed = Str(args, 1);
                cfg.EntryFee = packed == "-" ? new List<RewardItem>() : ItemSetUtil.Parse(packed);
                return (true, "entry fee: " + TournamentManager.DescribeItems(cfg.EntryFee));
            });

        public TextCommandResult AllowedClasses(TextCommandCallingArgs args)
            => Configure(args, (tour, cfg) =>
            {
                string raw = Str(args, 1) ?? "";
                if (raw == "-")
                {
                    cfg.AllowedLoadouts.Clear();
                    return (true, "every class is allowed");
                }
                var names = raw.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(n => n.Trim()).Where(n => n.Length > 0).ToList();
                if (names.Count == 0) return (false, "name at least one class, or '-' for all");
                cfg.AllowedLoadouts = names;
                return (true, "allowed classes: " + string.Join(", ", names));
            });

        public TextCommandResult Split(TextCommandCallingArgs args)
            => Configure(args, (tour, cfg) =>
            {
                var parts = (Str(args, 1) ?? "").Split(new[] { ',', ';', '/' }, StringSplitOptions.RemoveEmptyEntries);
                var percents = new List<int>();
                foreach (var p in parts)
                {
                    if (!int.TryParse(p.Trim(), out int v) || v < 0) return (false, "bad percent: " + p);
                    percents.Add(v);
                }
                if (percents.Count == 0) return (false, "give at least one percent");
                cfg.PotSplitPercents = percents;
                return (true, "pot split: " + string.Join("/", percents));
            });

        public TextCommandResult Prize(TextCommandCallingArgs args)
            => Configure(args, (tour, cfg) =>
            {
                int place = Num(args, 1);
                if (place < 1 || place > 16) return (false, "place must be 1..16");
                string packed = Str(args, 2);

                while (cfg.PlacementPrizes.Count < place) cfg.PlacementPrizes.Add(new RewardTier());
                cfg.PlacementPrizes[place - 1] = new RewardTier
                {
                    Items = packed == "-" ? new List<RewardItem>() : ItemSetUtil.Parse(packed)
                };
                return (true, "prize for place " + place + ": "
                    + TournamentManager.DescribeItems(cfg.PlacementPrizes[place - 1].Items));
            });

        // Goes through TournamentDraft.ApplyTo like the panel, so clamping is shared. Reply shows the stored value.
        public TextCommandResult Set(TextCommandCallingArgs args)
        {
            string option = (Str(args, 1) ?? "").ToLowerInvariant();
            string value = Str(args, 2) ?? "";
            bool isNumber = int.TryParse(value, out int n);
            bool flag = value.Equals("true", StringComparison.OrdinalIgnoreCase)
                     || value.Equals("on", StringComparison.OrdinalIgnoreCase)
                     || value == "1";

            var draft = new Network.TournamentDraft();
            System.Func<Tournament, string> describe;
            string needsNumber = null;
            switch (option)
            {
                case "readycheck":
                    needsNumber = "seconds, 0 to disable the ready check";
                    draft.ReadyCheckSeconds = n;
                    describe = cfg => "ready check: " + cfg.ReadyCheckSeconds + "s";
                    break;
                case "maxentrants":
                    needsNumber = Network.TournamentDraft.MinEntrantCount + ".." + Network.TournamentDraft.MaxEntrantCount;
                    draft.MaxEntrants = n;
                    describe = cfg => "max entrants: " + cfg.MaxEntrants;
                    break;
                case "concurrent":
                    needsNumber = "1.." + Network.TournamentDraft.MaxConcurrent;
                    draft.MaxConcurrentMatches = n;
                    describe = cfg => "concurrent matches: " + cfg.MaxConcurrentMatches;
                    break;
                case "thirdplace":
                    draft.ThirdPlaceMatch = flag;
                    describe = cfg => "third-place match: " + cfg.ThirdPlaceMatch;
                    break;
                case "seed":
                    draft.SeedMode = value.StartsWith("elo", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                    describe = cfg => "seeding: " + (cfg.SeedMode == 1 ? "by Elo" : "random");
                    break;
                case "rated":
                    draft.RatedMode = flag ? 0 : 1;
                    describe = cfg => "rated: " + (cfg.RatedMode == 0 ? "as the event" : "forced off");
                    break;
                case "rake":
                    needsNumber = "0..100";
                    draft.RakePercent = n;
                    describe = cfg => "rake: " + cfg.RakePercent + "%";
                    break;
                case "manual":
                    draft.ManualMatchStart = flag;
                    describe = cfg => cfg.ManualMatchStart
                        ? "matches are called by hand (/tournament next)"
                        : "matches are called automatically";
                    break;
                case "owngear":
                    draft.BringOwnGear = flag;
                    describe = cfg => cfg.BringOwnGear
                        ? "players bring their own gear - and lose it if they die"
                        : "players get the arena kit";
                    break;
                case "lockclass":
                    draft.LockLoadout = flag;
                    describe = cfg => cfg.LockLoadout
                        ? "class locked once the bracket starts"
                        : "players may change class between rounds";
                    break;
                case "autostart":
                    needsNumber = "entrant count, 0 to disable";
                    draft.AutoStartEntrants = n;
                    describe = cfg => cfg.AutoStartEntrants == 0
                        ? "auto-start off"
                        : "auto-start at " + cfg.AutoStartEntrants + " entrants";
                    break;
                case "autostartdelay":
                    needsNumber = "seconds";
                    draft.AutoStartDelaySeconds = n;
                    describe = cfg => "auto-start delay: " + cfg.AutoStartDelaySeconds + "s";
                    break;
                case "advance":
                    needsNumber = "how many go through, 1.." + Network.TournamentDraft.MaxAdvance;
                    draft.AdvancePerMatch = n;
                    describe = cfg => "advancing per match: " + cfg.AdvancePerMatch;
                    break;
                case "sides":
                    needsNumber = "sides per match, 2.." + Network.TournamentDraft.MaxSides + ", 0 = take it from the event";
                    draft.PartiesPerMatch = n;
                    describe = cfg => cfg.PartiesPerMatch == 0
                        ? "sides per match: from the event"
                        : "sides per match: " + cfg.PartiesPerMatch;
                    break;
                case "scheduler":
                    draft.PauseScheduler = flag;
                    describe = cfg => "pause auto-scheduler while running: " + cfg.PauseScheduler;
                    break;
                case "deadline":
                    needsNumber = "minutes from now, 0 for none";
                    draft.SignupDeadlineMinutes = n;
                    describe = cfg => cfg.SignupDeadlineUnix == 0 ? "no signup deadline" : "signup closes in " + n + " min";
                    break;

                case "openin":
                    needsNumber = "minutes from now, 0 to open by hand";
                    draft.OpenInMinutes = n;
                    describe = cfg => cfg.OpenAtUnix == 0 ? "opens by hand" : "signup opens in " + n + " min";
                    break;
                case "signupwindow":
                    // A duration, so repeating tournaments can re-derive it.
                    needsNumber = "minutes, 0 for none";
                    draft.SignupWindowMinutes = n;
                    describe = cfg => cfg.SignupWindowMinutes == 0
                        ? "no signup window"
                        : "signup stays open " + cfg.SignupWindowMinutes + " min";
                    break;
                case "announce":
                    draft.ShowBeforeSignup = flag;
                    describe = cfg => cfg.ShowBeforeSignup
                        ? "listed for players before signup opens"
                        : "hidden from players until signup opens";
                    break;
                case "repeat":
                    needsNumber = "minutes between runs, 0 = one-off";
                    draft.RepeatEveryMinutes = n;
                    describe = cfg => cfg.RepeatEveryMinutes == 0
                        ? "one-off"
                        : "repeats every " + cfg.RepeatEveryMinutes + " min after it ends";
                    break;
                case "repeatat":
                {
                    // "19:00,21:30" or "off".
                    bool off = value.Equals("off", StringComparison.OrdinalIgnoreCase) || value == "-" || value == "0";
                    // Parsed here only for a nicer error; ApplyTo parses it again.
                    var minutes = off ? new List<int>() : TournamentManager.ParseTimesOfDay(value);
                    if (minutes == null || (!off && minutes.Count == 0))
                        return TextCommandResult.Error("times as HH:MM[,HH:MM...] in server local time, or 'off'");
                    draft.RepeatAtPacked = off ? "" : value;
                    describe = cfg => cfg.RepeatAtMinutesOfDay.Count == 0
                        ? "no daily times"
                        : "repeats daily at " + TournamentManager.PackTimesOfDay(cfg.RepeatAtMinutesOfDay).Replace(";", ", ");
                    break;
                }
                default:
                    return TextCommandResult.Error("readycheck|maxentrants|concurrent|thirdplace|seed|rated|rake|sides|advance"
                        + "|manual|owngear|lockclass|autostart|autostartdelay|scheduler|deadline"
                        + "|openin|signupwindow|repeat|repeatat|announce");
            }

            // No option takes a negative value.
            if (needsNumber != null && (!isNumber || n < 0)) return TextCommandResult.Error(needsNumber);

            return Configure(args, (tour, cfg) =>
            {
                var applied = draft.ApplyTo(cfg);
                if (!applied.ok) return applied;
                return (true, describe(cfg));
            });
        }

        public TextCommandResult Open(TextCommandCallingArgs args)
            => Report(Tournaments?.OpenSignup(Id(args, 0)) ?? Unavailable);

        public TextCommandResult Close(TextCommandCallingArgs args)
            => Report(Tournaments?.CloseSignup(Id(args, 0)) ?? Unavailable);

        public TextCommandResult Begin(TextCommandCallingArgs args)
            => Report(Tournaments?.Begin(Id(args, 0)) ?? Unavailable);

        public TextCommandResult Cancel(TextCommandCallingArgs args)
        {
            bool refund = string.Equals(Str(args, 1), "refund", StringComparison.OrdinalIgnoreCase);
            return Report(Tournaments?.Cancel(Id(args, 0), refund) ?? Unavailable);
        }

        public TextCommandResult Result(TextCommandCallingArgs args)
            => Report(Tournaments?.SetMatchResult(Id(args, 0), Num(args, 1), Str(args, 2)) ?? Unavailable);

        public TextCommandResult CallNext(TextCommandCallingArgs args)
        {
            var t = Tournaments;
            if (t == null) return Report(Unavailable);
            long id = ResolveId(args, 0);
            if (id <= 0) return TextCommandResult.Error("which tournament? see /tournament list");
            return Report(t.ForceCallNext(id));
        }

        public TextCommandResult AddBots(TextCommandCallingArgs args)
            => Report(Tournaments?.AddBots(Id(args, 0), Num(args, 1)) ?? Unavailable);

        public TextCommandResult Disqualify(TextCommandCallingArgs args)
            => Report(Tournaments?.Disqualify(Id(args, 0), Str(args, 1)) ?? Unavailable);

        private static (bool ok, string message) Unavailable => (false, "tournaments are not available");

        private TextCommandResult Run(TextCommandCallingArgs args,
            System.Func<TournamentManager, long, IServerPlayer, (bool ok, string message)> op)
        {
            var t = Tournaments;
            if (t == null) return Report(Unavailable);
            if (!(args.Caller.Player is IServerPlayer player)) return TextCommandResult.Error("players only");

            long id = ResolveId(args, 0);
            if (id <= 0) return TextCommandResult.Error("which tournament? see /tournament list");
            return Report(op(t, id, player));
        }

        private TextCommandResult Configure(TextCommandCallingArgs args,
            System.Func<TournamentManager, Tournament, (bool ok, string message)> edit)
        {
            var t = Tournaments;
            if (t == null) return Report(Unavailable);
            return Report(t.Configure(Id(args, 0), cfg => edit(t, cfg)));
        }

        // Falls back to the only active tournament.
        private long ResolveId(TextCommandCallingArgs args, int index)
        {
            long typed = Id(args, index);
            return typed > 0 ? typed : (Tournaments?.SoleActiveId() ?? 0);
        }

        private static string Describe(List<long> ids)
            => ids == null || ids.Count == 0 ? "any suitable" : string.Join(", ", ids);

        private static TextCommandResult Lines(List<string> lines)
        {
            if (lines == null || lines.Count == 0) return TextCommandResult.Error("nothing to show");
            return TextCommandResult.Success(string.Join("\n", lines));
        }
    }
}
