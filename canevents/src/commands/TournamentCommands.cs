using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers.events;
using canevents.src.Tournaments;
using Vintagestory.API.Common;
using Vintagestory.API.Common.CommandAbbr;
using Vintagestory.API.Server;

namespace canevents.src.commands
{
    /// <summary>
    /// Chat access to the tournament driver. The player subcommands need no privilege; everything that
    /// shapes or runs a bracket requires controlserver.
    /// </summary>
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

                    // ── admin ──
                    .BeginSub("create")
                        .RequiresPrivilege(Privilege.controlserver)
                        .WithArgs(parsers.Word("name"), parsers.Word("eventCodes"), parsers.OptionalInt("teamSize"))
                        .HandleWith(impl.Create)
                        .WithDesc("Create a tournament. eventCodes is comma-separated.")
                    .EndSub()
                    .BeginSub("arena")
                        .RequiresPrivilege(Privilege.controlserver)
                        .WithArgs(parsers.Long("tournamentId"), parsers.Word("action"), parsers.OptionalLong("placeId"))
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
                        .WithDesc("readycheck|maxentrants|concurrent|thirdplace|seed|rated|rake|sides|scheduler|deadline"
                            + "|openin|signupwindow|repeat|repeatat|announce <value>")
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

        // read-only

        public TextCommandResult List(TextCommandCallingArgs args)
            => Lines(Tournaments?.Describe());

        public TextCommandResult Bracket(TextCommandCallingArgs args)
        {
            var t = Tournaments;
            if (t == null) return TextCommandResult.Error("tournaments are not available");
            long id = ResolveId(args, 0);
            if (id <= 0) return TextCommandResult.Error("which tournament? see /tournament list");
            return Lines(t.DescribeBracket(id));
        }

        // player

        public TextCommandResult Signup(TextCommandCallingArgs args)
            => Run(args, (t, id, player) => t.Signup(player, id));

        public TextCommandResult Withdraw(TextCommandCallingArgs args)
            => Run(args, (t, id, player) => t.Withdraw(player, id));

        public TextCommandResult TeamName(TextCommandCallingArgs args)
        {
            var t = Tournaments;
            if (t == null) return TextCommandResult.Error("tournaments are not available");
            if (!(args.Caller.Player is IServerPlayer player)) return TextCommandResult.Error("players only");
            return Report(t.SetTeamName(player, Id(args, 0), Str(args, 1)));
        }

        public TextCommandResult ClassChoice(TextCommandCallingArgs args)
        {
            var t = Tournaments;
            if (t == null) return TextCommandResult.Error("tournaments are not available");
            if (!(args.Caller.Player is IServerPlayer player)) return TextCommandResult.Error("players only");
            return Report(t.SetLoadout(player, Id(args, 0), Str(args, 1)));
        }

        public TextCommandResult Delete(TextCommandCallingArgs args)
            => Report(Tournaments?.Delete(Id(args, 0)) ?? Unavailable);

        public TextCommandResult Ready(TextCommandCallingArgs args)
            => Run(args, (t, id, player) => t.SetReady(player, id, 0, true));

        public TextCommandResult Forfeit(TextCommandCallingArgs args)
            => Run(args, (t, id, player) => t.SetReady(player, id, 0, false));

        // admin

        public TextCommandResult Create(TextCommandCallingArgs args)
        {
            var t = Tournaments;
            if (t == null) return TextCommandResult.Error("tournaments are not available");
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
                        if (placeId <= 0) return (false, "which arena?");
                        if (!cfg.PlaceWhitelist.Contains(placeId)) cfg.PlaceWhitelist.Add(placeId);
                        return (true, "arenas: " + Describe(cfg.PlaceWhitelist));
                    case "remove":
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

        public TextCommandResult Set(TextCommandCallingArgs args)
            => Configure(args, (tour, cfg) =>
            {
                string option = (Str(args, 1) ?? "").ToLowerInvariant();
                string value = Str(args, 2) ?? "";
                bool isNumber = int.TryParse(value, out int n);
                bool flag = value.Equals("true", StringComparison.OrdinalIgnoreCase)
                         || value.Equals("on", StringComparison.OrdinalIgnoreCase)
                         || value == "1";

                switch (option)
                {
                    case "readycheck":
                        if (!isNumber || n < 0) return (false, "seconds, 0 to disable the ready check");
                        cfg.ReadyCheckSeconds = n;
                        return (true, "ready check: " + n + "s");
                    // The upper bounds matter: these counts size the bracket that gets built, so an
                    // absurd number is an out-of-memory typo.
                    case "maxentrants":
                        if (!isNumber || n < 2 || n > 256) return (false, "2..256");
                        cfg.MaxEntrants = n;
                        return (true, "max entrants: " + n);
                    case "concurrent":
                        if (!isNumber || n < 1 || n > 32) return (false, "1..32");
                        cfg.MaxConcurrentMatches = n;
                        return (true, "concurrent matches: " + n);
                    case "thirdplace":
                        cfg.ThirdPlaceMatch = flag;
                        return (true, "third-place match: " + flag);
                    case "seed":
                        cfg.SeedMode = value.StartsWith("elo", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                        return (true, "seeding: " + (cfg.SeedMode == 1 ? "by Elo" : "random"));
                    case "rated":
                        cfg.RatedMode = flag ? 0 : 1;
                        return (true, "rated: " + (cfg.RatedMode == 0 ? "as the event" : "forced off"));
                    case "rake":
                        if (!isNumber || n < 0 || n > 100) return (false, "0..100");
                        cfg.RakePercent = n;
                        return (true, "rake: " + n + "%");
                    case "manual":
                        cfg.ManualMatchStart = flag;
                        return (true, flag
                            ? "matches are called by hand (/tournament next)"
                            : "matches are called automatically");
                    case "owngear":
                        cfg.BringOwnGear = flag;
                        return (true, flag
                            ? "players bring their own gear - and lose it if they die"
                            : "players get the arena kit");
                    case "lockclass":
                        cfg.LockLoadout = flag;
                        return (true, flag
                            ? "class locked once the bracket starts"
                            : "players may change class between rounds");
                    case "autostart":
                        if (!isNumber || n < 0) return (false, "entrant count, 0 to disable");
                        cfg.AutoStartEntrants = n;
                        return (true, n == 0
                            ? "auto-start off"
                            : "auto-start at " + n + " entrants");
                    case "autostartdelay":
                        if (!isNumber || n < 0) return (false, "seconds");
                        cfg.AutoStartDelaySeconds = n;
                        return (true, "auto-start delay: " + n + "s");
                    case "advance":
                        if (!isNumber || n < 1 || n > 16) return (false, "how many go through, 1..16");
                        cfg.AdvancePerMatch = n;
                        return (true, "advancing per match: " + n);
                    case "sides":
                        if (!isNumber || n < 0 || n > 16) return (false, "sides per match, 2..16, 0 = take it from the event");
                        cfg.PartiesPerMatch = n == 0 ? 0 : Math.Max(2, n);
                        return (true, cfg.PartiesPerMatch == 0
                            ? "sides per match: from the event"
                            : "sides per match: " + cfg.PartiesPerMatch);
                    case "scheduler":
                        cfg.PauseScheduler = flag;
                        return (true, "pause auto-scheduler while running: " + flag);
                    case "deadline":
                        if (!isNumber || n < 0) return (false, "minutes from now, 0 for none");
                        cfg.SignupDeadlineUnix = n == 0 ? 0 : TournamentManager.Now + n * 60;
                        return (true, n == 0 ? "no signup deadline" : "signup closes in " + n + " min");

                    // scheduling
                    case "openin":
                        if (!isNumber || n < 0) return (false, "minutes from now, 0 to open by hand");
                        cfg.OpenAtUnix = n == 0 ? 0 : TournamentManager.Now + n * 60;
                        return (true, n == 0
                            ? "opens by hand"
                            : "signup opens in " + n + " min");
                    case "signupwindow":
                        // A duration, not a moment, so a repeating tournament can re-derive it.
                        if (!isNumber || n < 0 || n > 7 * 24 * 60) return (false, "minutes, 0 for none");
                        cfg.SignupWindowMinutes = n;
                        return (true, n == 0 ? "no signup window" : "signup stays open " + n + " min");
                    case "announce":
                        cfg.ShowBeforeSignup = flag;
                        return (true, flag
                            ? "listed for players before signup opens"
                            : "hidden from players until signup opens");
                    case "repeat":
                        if (!isNumber || n < 0 || n > 30 * 24 * 60) return (false, "minutes between runs, 0 = one-off");
                        cfg.RepeatEveryMinutes = n;
                        return (true, n == 0 ? "one-off" : "repeats every " + n + " min after it ends");
                    case "repeatat":
                    {
                        // "19:00,21:30", or "off".
                        if (value.Equals("off", StringComparison.OrdinalIgnoreCase) || value == "-" || value == "0")
                        {
                            cfg.RepeatAtMinutesOfDay.Clear();
                            return (true, "no daily times");
                        }
                        // Same parser as the admin panel, so the two cannot disagree.
                        var minutes = TournamentManager.ParseTimesOfDay(value);
                        if (minutes == null || minutes.Count == 0)
                            return (false, "times as HH:MM[,HH:MM...] in server local time, or 'off'");
                        cfg.RepeatAtMinutesOfDay = minutes;
                        return (true, "repeats daily at "
                            + TournamentManager.PackTimesOfDay(minutes).Replace(";", ", "));
                    }
                    default:
                        return (false, "readycheck|maxentrants|concurrent|thirdplace|seed|rated|rake|sides|advance"
                            + "|manual|owngear|lockclass|autostart|autostartdelay|scheduler|deadline"
                            + "|openin|signupwindow|repeat|repeatat|announce");
                }
            });

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
            if (t == null) return TextCommandResult.Error("tournaments are not available");
            long id = ResolveId(args, 0);
            if (id <= 0) return TextCommandResult.Error("which tournament? see /tournament list");
            return Report(t.ForceCallNext(id));
        }

        public TextCommandResult AddBots(TextCommandCallingArgs args)
            => Report(Tournaments?.AddBots(Id(args, 0), Num(args, 1)) ?? Unavailable);

        public TextCommandResult Disqualify(TextCommandCallingArgs args)
            => Report(Tournaments?.Disqualify(Id(args, 0), Str(args, 1)) ?? Unavailable);

        // plumbing

        private static (bool ok, string message) Unavailable => (false, "tournaments are not available");

        private TextCommandResult Run(TextCommandCallingArgs args,
            System.Func<TournamentManager, long, IServerPlayer, (bool ok, string message)> op)
        {
            var t = Tournaments;
            if (t == null) return TextCommandResult.Error("tournaments are not available");
            if (!(args.Caller.Player is IServerPlayer player)) return TextCommandResult.Error("players only");

            long id = ResolveId(args, 0);
            if (id <= 0) return TextCommandResult.Error("which tournament? see /tournament list");
            return Report(op(t, id, player));
        }

        private TextCommandResult Configure(TextCommandCallingArgs args,
            System.Func<TournamentManager, Tournament, (bool ok, string message)> edit)
        {
            var t = Tournaments;
            if (t == null) return TextCommandResult.Error("tournaments are not available");
            return Report(t.Configure(Id(args, 0), cfg => edit(t, cfg)));
        }

        /// <summary>The id the player typed, or the only active tournament when they typed none.</summary>
        private long ResolveId(TextCommandCallingArgs args, int index)
        {
            long typed = Id(args, index);
            return typed > 0 ? typed : (Tournaments?.SoleActiveId() ?? 0);
        }

        // An omitted optional parser yields null, so every read is defensive rather than a cast that
        // throws on a bare subcommand.
        private static object Raw(TextCommandCallingArgs args, int index)
            => args.Parsers != null && args.Parsers.Count > index ? args.Parsers[index].GetValue() : null;

        private static string Str(TextCommandCallingArgs args, int index) => Raw(args, index) as string;

        private static long Id(TextCommandCallingArgs args, int index)
        {
            var raw = Raw(args, index);
            return raw == null ? 0 : Convert.ToInt64(raw);
        }

        private static int Num(TextCommandCallingArgs args, int index)
        {
            var raw = Raw(args, index);
            return raw == null ? 0 : Convert.ToInt32(raw);
        }

        private static string Describe(List<long> ids)
            => ids == null || ids.Count == 0 ? "any suitable" : string.Join(", ", ids);

        private static TextCommandResult Report((bool ok, string message) result)
            => result.ok ? TextCommandResult.Success(result.message) : TextCommandResult.Error(result.message);

        private static TextCommandResult Lines(List<string> lines)
        {
            if (lines == null || lines.Count == 0) return TextCommandResult.Error("nothing to show");
            return TextCommandResult.Success(string.Join("\n", lines));
        }
    }
}
