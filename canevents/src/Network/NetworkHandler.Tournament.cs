using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers.events;
using canevents.src.Tournaments;
using Newtonsoft.Json;
using Vintagestory.API.Server;

namespace canevents.src.Network
{
    /// <summary>
    /// Server side of the tournament UI: players browse and sign up, admins configure and drive.
    /// Everything that shapes a bracket goes through RequireAdmin. Pushes are targeted - the list
    /// goes per-viewer to everyone online, a bracket only to those currently looking at it.
    /// </summary>
    public partial class ServerNetworkHandler
    {
        private TournamentManager Tournaments => eventManager?.Tournaments;

        /// <summary>Who is watching which bracket, so a change can be pushed without polling.</summary>
        private readonly Dictionary<long, HashSet<string>> bracketViewers = new Dictionary<long, HashSet<string>>();

        // Requests

        private void OnRequestTournamentList(IServerPlayer player, RequestTournamentListMsg msg)
            => SendTournamentList(player, msg?.IncludeHistory ?? false);

        private void OnRequestTournamentBracket(IServerPlayer player, RequestTournamentBracketMsg msg)
        {
            // Subscribe only to a bracket that exists: an unvalidated id lets a client mint a viewer
            // set per packet and grow the dictionary without bound.
            bool known = Tournaments?.BuildBracketMsg(msg.TournamentId) != null;
            lock (bracketViewers)
            {
                // One bracket at a time per viewer: opening another replaces the subscription, and
                // the sets it empties are dropped so the dictionary tracks live brackets only.
                foreach (var key in bracketViewers.Keys.ToList())
                {
                    bracketViewers[key].Remove(player.PlayerUID);
                    if (bracketViewers[key].Count == 0) bracketViewers.Remove(key);
                }
                if (known)
                {
                    if (!bracketViewers.TryGetValue(msg.TournamentId, out var viewers))
                        bracketViewers[msg.TournamentId] = viewers = new HashSet<string>();
                    viewers.Add(player.PlayerUID);
                }
            }
            if (!known) return;
            SendTournamentBracket(player, msg.TournamentId);
        }

        private void OnTournamentSignup(IServerPlayer player, TournamentSignupMsg msg)
        {
            var t = Tournaments;
            if (t == null) { ReplyFail(player, "Tournaments are not available."); return; }

            var result = msg.LoadoutOnly
                ? t.SetLoadout(player, msg.TournamentId, msg.LoadoutSet)
                : msg.RenameOnly
                ? t.SetTeamName(player, msg.TournamentId, msg.TeamName)
                : msg.Join
                    ? t.Signup(player, msg.TournamentId, msg.TeamName)
                    : t.Withdraw(player, msg.TournamentId);
            if (result.ok) ReplyOk(player, result.message); else ReplyFail(player, result.message);
            // Only broadcast on an actual change: on failure this would turn one spammed request
            // into a per-online-player push.
            if (result.ok) BroadcastTournamentUpdate(msg.TournamentId);
        }

        private void OnTournamentReadyResponse(IServerPlayer player, TournamentReadyResponseMsg msg)
        {
            var t = Tournaments;
            if (t == null) return;

            var result = t.SetReady(player, msg.TournamentId, msg.MatchId, msg.Accept);
            if (!result.ok) { ReplyFail(player, result.message); return; }
            BroadcastTournamentUpdate(msg.TournamentId);
        }

        private void OnTournamentAdminAction(IServerPlayer player, TournamentAdminActionMsg msg)
        {
            if (!RequireAdmin(player)) return;
            var t = Tournaments;
            if (t == null) { ReplyFail(player, "Tournaments are not available."); return; }

            (bool ok, string message) result;
            switch ((msg.Action ?? "").ToLowerInvariant())
            {
                case "create":
                    {
                        var draft = ParseDraft(msg.ConfigJson);
                        if (draft == null) { ReplyFail(player, "Bad tournament config."); return; }
                        result = t.Create(draft.Name, draft.EventCodes, draft.TeamSize ?? 1);
                        // Apply the rest of the draft to the tournament just created.
                        if (result.ok)
                        {
                            long newId = t.NewestId();
                            var applied = t.Configure(newId, cfg => ApplyDraft(cfg, draft));
                            if (!applied.ok) result = applied;
                        }
                        break;
                    }
                case "configure":
                    {
                        var draft = ParseDraft(msg.ConfigJson);
                        if (draft == null) { ReplyFail(player, "Bad tournament config."); return; }
                        result = t.Configure(msg.TournamentId, cfg => ApplyDraft(cfg, draft));
                        break;
                    }
                case "opensignup": result = t.OpenSignup(msg.TournamentId); break;
                case "closesignup": result = t.CloseSignup(msg.TournamentId); break;
                case "begin": result = t.Begin(msg.TournamentId); break;
                case "cancel": result = t.Cancel(msg.TournamentId, msg.BoolArg); break;
                case "delete": result = t.Delete(msg.TournamentId); break;
                // Testing helpers - MatchId doubles as the count for "bots".
                case "bots": result = t.AddBots(msg.TournamentId, msg.MatchId); break;
                case "forcecall": result = t.ForceCallNext(msg.TournamentId); break;
                case "result": result = t.SetMatchResult(msg.TournamentId, msg.MatchId, msg.StringArg); break;
                case "dq": result = t.Disqualify(msg.TournamentId, msg.StringArg); break;
                default: result = (false, "Unknown tournament action: " + msg.Action); break;
            }

            if (result.ok) ReplyOk(player, result.message); else ReplyFail(player, result.message);
            BroadcastTournamentUpdate(msg.TournamentId);
        }

        // Pushes

        public void SendTournamentList(IServerPlayer player, bool includeHistory = false)
        {
            var t = Tournaments;
            if (t == null) return;
            serverChannel.SendPacket(t.BuildListMsg(player.PlayerUID, includeHistory), player);
        }

        public void SendTournamentBracket(IServerPlayer player, long tournamentId)
        {
            var t = Tournaments;
            if (t == null) return;
            var msg = t.BuildBracketMsg(tournamentId);
            if (msg != null) serverChannel.SendPacket(msg, player);
        }

        /// <summary>Re-sends the list to everyone online and the bracket to whoever has it open,
        /// after any change.</summary>
        public void BroadcastTournamentUpdate(long tournamentId)
        {
            var t = Tournaments;
            if (t == null) return;

            foreach (var p in sapi.World.AllOnlinePlayers)
                if (p is IServerPlayer sp) serverChannel.SendPacket(t.BuildListMsg(sp.PlayerUID), sp);

            if (tournamentId <= 0) return;
            List<string> viewers;
            lock (bracketViewers)
            {
                if (!bracketViewers.TryGetValue(tournamentId, out var set)) return;
                viewers = set.ToList();
            }
            var bracket = t.BuildBracketMsg(tournamentId);
            if (bracket == null) return;
            foreach (var uid in viewers)
                if (sapi.World.PlayerByUid(uid) is IServerPlayer sp) serverChannel.SendPacket(bracket, sp);
        }

        public void SendTournamentReadyRequest(IServerPlayer player, TournamentReadyRequestMsg msg)
            => serverChannel.SendPacket(msg, player);

        public void SendTournamentReadyClosed(IServerPlayer player, long tournamentId, int matchId, string reason)
            => serverChannel.SendPacket(new TournamentReadyClosedMsg
            {
                TournamentId = tournamentId,
                MatchId = matchId,
                Reason = reason
            }, player);

        internal void DropBracketViewer(string uid)
        {
            lock (bracketViewers)
                foreach (var set in bracketViewers.Values) set.Remove(uid);
        }

        // Draft: the admin panel's config, carried as JSON

        private static TournamentDraft ParseDraft(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JsonConvert.DeserializeObject<TournamentDraft>(json); }
            catch { return null; }
        }

        /// <summary>Copies a draft over a tournament's configuration, ignoring fields left null.</summary>
        private static (bool ok, string message) ApplyDraft(Tournament cfg, TournamentDraft d)
        {
            if (!string.IsNullOrWhiteSpace(d.Name)) cfg.Name = d.Name.Trim();
            if (d.EventCodes != null && d.EventCodes.Count > 0) cfg.EventCodes = d.EventCodes;
            if (d.PlaceWhitelist != null) cfg.PlaceWhitelist = d.PlaceWhitelist;
            // Upper bounds too: these numbers size real allocations, so a hand-crafted packet with
            // 2e9 sides would otherwise be an out-of-memory button.
            if (d.TeamSize.HasValue) cfg.TeamSize = System.Math.Clamp(d.TeamSize.Value, 1, 16);
            if (d.MaxEntrants.HasValue) cfg.MaxEntrants = System.Math.Clamp(d.MaxEntrants.Value, 2, 256);
            if (d.EntryFee != null) cfg.EntryFee = d.EntryFee;
            if (d.RakePercent.HasValue) cfg.RakePercent = System.Math.Clamp(d.RakePercent.Value, 0, 100);
            if (d.PotSplitPercents != null && d.PotSplitPercents.Count > 0) cfg.PotSplitPercents = d.PotSplitPercents;
            if (d.PlacementPrizes != null) cfg.PlacementPrizes = d.PlacementPrizes;
            if (d.ThirdPlaceMatch.HasValue) cfg.ThirdPlaceMatch = d.ThirdPlaceMatch.Value;
            if (d.LockLoadout.HasValue) cfg.LockLoadout = d.LockLoadout.Value;
            if (d.AllowedLoadouts != null)
            {
                cfg.AllowedLoadouts = d.AllowedLoadouts;
                // Drop picks that are now banned; those players get an allowed class instead of
                // starting in something the rules forbid.
                foreach (var e in cfg.Entrants)
                    foreach (var uid in new List<string>(e.LoadoutChoices.Keys))
                        if (!cfg.AllowsLoadout(e.LoadoutChoices[uid])) e.LoadoutChoices.Remove(uid);
            }
            if (d.BringOwnGear.HasValue) cfg.BringOwnGear = d.BringOwnGear.Value;
            if (d.ManualMatchStart.HasValue) cfg.ManualMatchStart = d.ManualMatchStart.Value;
            if (d.PartiesPerMatch.HasValue)
                cfg.PartiesPerMatch = d.PartiesPerMatch.Value <= 0 ? 0 : System.Math.Clamp(d.PartiesPerMatch.Value, 2, 16);
            if (d.AdvancePerMatch.HasValue) cfg.AdvancePerMatch = System.Math.Clamp(d.AdvancePerMatch.Value, 1, 16);
            if (d.AutoStartEntrants.HasValue) cfg.AutoStartEntrants = System.Math.Max(0, d.AutoStartEntrants.Value);
            if (d.AutoStartDelaySeconds.HasValue)
                cfg.AutoStartDelaySeconds = System.Math.Max(0, d.AutoStartDelaySeconds.Value);
            if (d.ReadyCheckSeconds.HasValue) cfg.ReadyCheckSeconds = System.Math.Max(0, d.ReadyCheckSeconds.Value);
            if (d.MaxConcurrentMatches.HasValue) cfg.MaxConcurrentMatches = System.Math.Max(1, d.MaxConcurrentMatches.Value);
            if (d.SignupDeadlineMinutes.HasValue)
                cfg.SignupDeadlineUnix = d.SignupDeadlineMinutes.Value <= 0
                    ? 0 : TournamentManager.Now + d.SignupDeadlineMinutes.Value * 60;

            // OpenInMinutes is relative on the wire because the client's clock is not the server's;
            // the absolute moment is worked out here, where "now" is authoritative.
            if (d.OpenInMinutes.HasValue)
                cfg.OpenAtUnix = d.OpenInMinutes.Value <= 0
                    ? 0 : TournamentManager.Now + d.OpenInMinutes.Value * 60;
            if (d.SignupWindowMinutes.HasValue)
                cfg.SignupWindowMinutes = System.Math.Clamp(d.SignupWindowMinutes.Value, 0, 7 * 24 * 60);
            if (d.RepeatEveryMinutes.HasValue)
                cfg.RepeatEveryMinutes = System.Math.Clamp(d.RepeatEveryMinutes.Value, 0, 30 * 24 * 60);
            if (d.ShowBeforeSignup.HasValue) cfg.ShowBeforeSignup = d.ShowBeforeSignup.Value;
            if (d.RepeatAtPacked != null)
            {
                // A malformed schedule is refused whole: half a set of times would bring the
                // tournament back at hours nobody asked for.
                var times = TournamentManager.ParseTimesOfDay(d.RepeatAtPacked);
                if (times == null) return (false, "Repeat times must be HH:MM, separated by ';'.");
                cfg.RepeatAtMinutesOfDay = times;
            }
            if (d.SeedMode.HasValue) cfg.SeedMode = d.SeedMode.Value;
            if (d.RatedMode.HasValue) cfg.RatedMode = d.RatedMode.Value;
            if (d.PauseScheduler.HasValue) cfg.PauseScheduler = d.PauseScheduler.Value;
            return (true, "Saved.");
        }
    }

    /// <summary>The admin panel's editable view of a tournament, carried as JSON on
    /// TournamentAdminActionMsg. Nullable everywhere so a partial edit leaves the rest alone.</summary>
    public class TournamentDraft
    {
        public string Name { get; set; }
        public List<string> EventCodes { get; set; }
        public List<long> PlaceWhitelist { get; set; }
        public int? TeamSize { get; set; }
        public int? MaxEntrants { get; set; }
        public List<RewardItem> EntryFee { get; set; }
        public int? RakePercent { get; set; }
        public List<int> PotSplitPercents { get; set; }
        public List<RewardTier> PlacementPrizes { get; set; }
        public bool? ThirdPlaceMatch { get; set; }
        public bool? LockLoadout { get; set; }
        public List<string> AllowedLoadouts { get; set; }
        public bool? BringOwnGear { get; set; }
        public bool? ManualMatchStart { get; set; }
        public int? PartiesPerMatch { get; set; }
        public int? AdvancePerMatch { get; set; }
        public int? AutoStartEntrants { get; set; }
        public int? AutoStartDelaySeconds { get; set; }
        public int? ReadyCheckSeconds { get; set; }
        public int? MaxConcurrentMatches { get; set; }
        public int? SignupDeadlineMinutes { get; set; }
        public int? SeedMode { get; set; }
        public int? RatedMode { get; set; }
        public bool? PauseScheduler { get; set; }

        // Scheduling in minutes-from-now rather than timestamps: only the server's clock is
        // authoritative for when things happen.
        public int? OpenInMinutes { get; set; }
        public int? SignupWindowMinutes { get; set; }
        public int? RepeatEveryMinutes { get; set; }
        /// <summary>"19:00;21:30", or "" to clear. Null leaves the schedule alone.</summary>
        public string RepeatAtPacked { get; set; }
        public bool? ShowBeforeSignup { get; set; }
    }
}
