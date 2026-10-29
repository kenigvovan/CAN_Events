using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers.events;
using canevents.src.Tournaments;
using Newtonsoft.Json;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace canevents.src.Network
{
    public partial class ServerNetworkHandler
    {
        private TournamentManager Tournaments => eventManager?.Tournaments;

        // tournament id -> uids with that bracket open.
        private readonly Dictionary<long, HashSet<string>> bracketViewers = new Dictionary<long, HashSet<string>>();

        private void OnRequestTournamentList(IServerPlayer player, RequestTournamentListMsg msg)
            => SendTournamentList(player, msg?.IncludeHistory ?? false);

        private void OnRequestTournamentBracket(IServerPlayer player, RequestTournamentBracketMsg msg)
        {
            // Don't let clients add entries for made-up ids.
            bool known = Tournaments?.FindForView(msg.TournamentId) != null;
            lock (bracketViewers)
            {
                // One bracket per viewer; drop sets that become empty.
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

        // Player-side handlers reply on the main channel.
        private void OnTournamentSignup(IServerPlayer player, TournamentSignupMsg msg)
            => PlayerTournamentAction(player, msg.TournamentId, t => msg.Join
                ? t.Signup(player, msg.TournamentId, msg.TeamName)
                : t.Withdraw(player, msg.TournamentId));

        private void OnTournamentRename(IServerPlayer player, TournamentRenameMsg msg)
            => PlayerTournamentAction(player, msg.TournamentId,
                t => t.SetTeamName(player, msg.TournamentId, msg.TeamName));

        private void OnTournamentLoadout(IServerPlayer player, TournamentLoadoutMsg msg)
            => PlayerTournamentAction(player, msg.TournamentId,
                t => t.SetLoadout(player, msg.TournamentId, msg.SetName));

        private void PlayerTournamentAction(IServerPlayer player, long tournamentId,
            System.Func<TournamentManager, (bool ok, string message)> action)
        {
            var t = Tournaments;
            if (t == null) { ReplyFail(player, Lang.Get("canevents:tournaments-unavailable"), ProtocolChannel.Main); return; }

            // TournamentManager.Transition broadcasts on success.
            var result = action(t);
            Reply(player, result.ok, result.message, 0, ProtocolChannel.Main);
        }

        private void OnTournamentReadyResponse(IServerPlayer player, TournamentReadyResponseMsg msg)
        {
            var t = Tournaments;
            if (t == null) return;

            var result = t.SetReady(player, msg.TournamentId, msg.MatchId, msg.Accept);
            if (!result.ok) ReplyFail(player, result.message, ProtocolChannel.Main);
        }

        private void OnTournamentAdminAction(IServerPlayer player, TournamentAdminActionMsg msg)
        {
            if (!RequireAdmin(player)) return;
            var t = Tournaments;
            if (t == null) { ReplyFail(player, Lang.Get("canevents:tournaments-unavailable")); return; }

            (bool ok, string message) result;
            switch ((msg.Action ?? "").ToLowerInvariant())
            {
                case "create":
                    {
                        var draft = ParseDraft(msg.ConfigJson);
                        if (draft == null) { ReplyFail(player, Lang.Get("canevents:tournament-bad-config")); return; }
                        result = t.Create(draft.Name, draft.EventCodes, draft.TeamSize ?? 1);
                        if (result.ok)
                        {
                            long newId = t.NewestId();
                            var applied = t.Configure(newId, cfg => draft.ApplyTo(cfg));
                            if (!applied.ok) result = applied;
                        }
                        break;
                    }
                case "configure":
                    {
                        var draft = ParseDraft(msg.ConfigJson);
                        if (draft == null) { ReplyFail(player, Lang.Get("canevents:tournament-bad-config")); return; }
                        result = t.Configure(msg.TournamentId, cfg => draft.ApplyTo(cfg));
                        break;
                    }
                case "opensignup": result = t.OpenSignup(msg.TournamentId); break;
                case "closesignup": result = t.CloseSignup(msg.TournamentId); break;
                case "begin": result = t.Begin(msg.TournamentId); break;
                case "cancel": result = t.Cancel(msg.TournamentId, msg.BoolArg); break;
                case "delete": result = t.Delete(msg.TournamentId); break;
                // Test helpers. For "bots", MatchId is the count.
                case "bots": result = t.AddBots(msg.TournamentId, msg.MatchId); break;
                case "forcecall": result = t.ForceCallNext(msg.TournamentId); break;
                case "result": result = t.SetMatchResult(msg.TournamentId, msg.MatchId, msg.StringArg); break;
                case "dq": result = t.Disqualify(msg.TournamentId, msg.StringArg); break;
                default: result = (false, Lang.Get("canevents:tournament-unknown-action", msg.Action)); break;
            }

            if (result.ok) ReplyOk(player, result.message); else ReplyFail(player, result.message);
        }

        public void SendTournamentList(IServerPlayer player, bool includeHistory = false)
        {
            var t = Tournaments;
            if (t == null) return;
            serverChannel.SendPacket(BuildTournamentList(t, player.PlayerUID, includeHistory), player);
        }

        public void SendTournamentBracket(IServerPlayer player, long tournamentId)
        {
            var t = Tournaments;
            if (t == null) return;
            var msg = BuildTournamentBracket(t, tournamentId, RequireAdmin(player));
            if (msg != null) serverChannel.SendPacket(msg, player);
        }

        // List to everyone online, bracket only to its viewers.
        public void BroadcastTournamentUpdate(long tournamentId)
        {
            var t = Tournaments;
            if (t == null) return;

            foreach (var p in sapi.World.AllOnlinePlayers)
                if (p is IServerPlayer sp) serverChannel.SendPacket(BuildTournamentList(t, sp.PlayerUID), sp);

            if (tournamentId <= 0) return;
            List<string> viewers;
            lock (bracketViewers)
            {
                if (!bracketViewers.TryGetValue(tournamentId, out var set)) return;
                viewers = set.ToList();
            }
            // Built lazily, admin variant includes editor settings.
            TournamentBracketMsg forPlayers = null, forAdmins = null;
            foreach (var uid in viewers)
            {
                if (!(sapi.World.PlayerByUid(uid) is IServerPlayer sp)) continue;
                bool admin = RequireAdmin(sp);
                var bracket = admin
                    ? forAdmins ?? (forAdmins = BuildTournamentBracket(t, tournamentId, true))
                    : forPlayers ?? (forPlayers = BuildTournamentBracket(t, tournamentId, false));
                if (bracket == null) return;
                serverChannel.SendPacket(bracket, sp);
            }
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

        private static TournamentDraft ParseDraft(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JsonConvert.DeserializeObject<TournamentDraft>(json); }
            catch { return null; }
        }

        // Per viewer, YouAreIn differs.
        private static TournamentListMsg BuildTournamentList(TournamentManager manager, string viewerUid,
            bool includeHistory = false)
        {
            var list = manager.ListFor(viewerUid, includeHistory);
            int n = list.Count;
            var msg = new TournamentListMsg
            {
                Ids = new long[n],
                Names = new string[n],
                States = new int[n],
                EventCodesPacked = new string[n],
                EntrantCounts = new int[n],
                MaxEntrants = new int[n],
                EntryFeePacked = new string[n],
                PotPacked = new string[n],
                YouAreIn = new bool[n],
                SignupDeadlineUnix = new long[n],
                TeamSizes = new int[n],
                OpenAtUnix = new long[n]
            };

            for (int i = 0; i < n; i++)
            {
                var t = list[i];
                msg.Ids[i] = t.Id;
                msg.Names[i] = t.Name;
                msg.States[i] = (int)t.State;
                msg.EventCodesPacked[i] = PackedList.Pack(t.EventCodes);
                msg.EntrantCounts[i] = t.Entrants.Count;
                msg.MaxEntrants[i] = t.MaxEntrants;
                msg.EntryFeePacked[i] = ItemSetUtil.Pack(t.EntryFee);
                msg.PotPacked[i] = PackPot(t.Pot);
                msg.YouAreIn[i] = t.EntrantOfPlayer(viewerUid) != null;
                msg.SignupDeadlineUnix[i] = t.SignupDeadlineUnix;
                msg.TeamSizes[i] = t.TeamSize;
                msg.OpenAtUnix[i] = t.OpenAtUnix;
            }
            return msg;
        }

        // Null for an unknown id.
        private static TournamentBracketMsg BuildTournamentBracket(TournamentManager manager, long id, bool forAdmin)
        {
            var t = manager.FindForView(id);
            if (t == null) return null;

            int e = t.Entrants.Count;
            int m = t.Matches.Count;
            var msg = new TournamentBracketMsg
            {
                TournamentId = t.Id,
                Name = t.Name,
                State = (int)t.State,

                EntrantIds = new string[e],
                EntrantNames = new string[e],
                EntrantSeeds = new int[e],
                EntrantMembers = new string[e],

                MatchIds = new int[m],
                MatchRounds = new int[m],
                MatchSlots = new int[m],
                MatchSlotsPacked = new string[m],
                MatchWinner = new string[m],
                MatchStates = new int[m],
                MatchFeedsPacked = new string[m],
                MatchAdvanceCount = new int[m],
                MatchIsThirdPlace = new bool[m],
                MatchEventCode = new string[m],
                MatchReadyDeadline = new long[m],
                MatchResultKind = new string[m],
                MatchAbsentPacked = new string[m],

                PotPacked = PackPot(t.Pot),
                PotSplitPercents = t.PotSplitPercents.ToArray(),
                PlacementPrizesPacked = t.PlacementPrizes
                    .Select(tier => ItemSetUtil.Pack(tier?.Items)).ToArray(),
                FinalPlacementEntrantIds = t.FinalPlacement.ToArray(),
                EntryFeePacked = ItemSetUtil.Pack(t.EntryFee),
                RakePercent = t.RakePercent,
                ThirdPlaceMatch = t.ThirdPlaceMatch,
                MaxEntrants = t.MaxEntrants,
                PartiesPerMatch = t.Matches.Count > 0 ? t.Matches[0].Arity : manager.ResolveArity(t),
                AdvancePerMatch = t.Matches.Count > 0 ? t.Matches[0].AdvanceCount : t.AdvancePerMatch,
                AllowedLoadouts = t.AllowedLoadouts.ToArray(),

                OpenAtUnix = t.OpenAtUnix,
                SignupWindowMinutes = t.SignupWindowMinutes,
                RepeatEveryMinutes = t.RepeatEveryMinutes,
                RepeatAtPacked = TournamentManager.PackTimesOfDay(t.RepeatAtMinutesOfDay),
                ShowBeforeSignup = t.ShowBeforeSignup
            };

            if (forAdmin)
            {
                msg.HasAdminSettings = true;
                msg.PlaceWhitelist = (t.PlaceWhitelist ?? new List<long>()).ToArray();
                msg.LockLoadout = t.LockLoadout;
                msg.BringOwnGear = t.BringOwnGear;
                msg.ManualMatchStart = t.ManualMatchStart;
                msg.ReadyCheckSeconds = t.ReadyCheckSeconds;
                msg.MaxConcurrentMatches = t.MaxConcurrentMatches;
                msg.AutoStartEntrants = t.AutoStartEntrants;
                msg.AutoStartDelaySeconds = t.AutoStartDelaySeconds;
                msg.SeedMode = t.SeedMode;
                msg.RatedMode = t.RatedMode;
                msg.PauseScheduler = t.PauseScheduler;
            }

            for (int i = 0; i < e; i++)
            {
                msg.EntrantIds[i] = t.Entrants[i].Id;
                msg.EntrantNames[i] = t.Entrants[i].DisplayName;
                msg.EntrantSeeds[i] = t.Entrants[i].Seed;
                msg.EntrantMembers[i] = t.Entrants[i].MembersText ?? "";
            }

            for (int i = 0; i < m; i++)
            {
                var bm = t.Matches[i];
                msg.MatchIds[i] = bm.Id;
                msg.MatchRounds[i] = bm.Round;
                msg.MatchSlots[i] = bm.SlotInRound;
                msg.MatchSlotsPacked[i] = PackedList.Pack(bm.Slots);
                msg.MatchWinner[i] = bm.WinnerEntrantId ?? "";
                msg.MatchStates[i] = (int)bm.State;
                msg.MatchFeedsPacked[i] = PackedList.Pack(
                    bm.Feeds.Select(f => f.MatchId + ":" + f.Slot));
                msg.MatchAdvanceCount[i] = bm.AdvanceCount;
                msg.MatchIsThirdPlace[i] = bm.IsThirdPlace;
                msg.MatchEventCode[i] = bm.EventCode ?? "";
                msg.MatchReadyDeadline[i] = bm.ReadyDeadlineUnix;
                msg.MatchResultKind[i] = bm.ResultKind ?? "";
                msg.MatchAbsentPacked[i] = PackedList.Pack(bm.AbsentSides);
            }
            return msg;
        }

        private static string PackPot(Dictionary<string, int> pot)
        {
            if (pot == null || pot.Count == 0) return "";
            return ItemSetUtil.Pack(pot.Select(kv => new RewardItem { Code = kv.Key, Quantity = kv.Value }).ToList());
        }
    }
}
