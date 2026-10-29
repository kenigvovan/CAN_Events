using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace canevents.src.Tournaments
{
    // Ready window: prompts, confirmations, missed deadlines.
    public partial class TournamentManager
    {
        public (bool ok, string message) SetReady(IServerPlayer player, long tournamentId, int matchId, bool accept)
        {
            return Transition(tournamentId, t =>
            {
                if (t.State != TournamentState.Running) return (false, "tournament is not running");

                var m = matchId > 0 ? t.FindMatch(matchId) : CalledMatchOf(t, player.PlayerUID);
                if (m == null) return (false, Lang.Get("canevents:tournament-no-called-match"));

                if (m.State == BracketMatchState.Starting || m.State == BracketMatchState.Live)
                    return (true, Lang.Get("canevents:tournament-ready-waiting"));

                if (m.State != BracketMatchState.AwaitingReady)
                    return (false, Lang.Get("canevents:tournament-no-called-match"));

                var side = SideOf(t, m, player.PlayerUID);
                if (side == null) return (false, Lang.Get("canevents:tournament-not-your-match"));

                if (!accept)
                {
                    // Repeated packets would re-announce the forfeit.
                    if (m.AbsentSides.Contains(side))
                        return (false, Lang.Get("canevents:tournament-already-forfeited"));

                    if (!IsEntrantLeader(t, side, player.PlayerUID, out string leaderName))
                        return (false, Lang.Get("canevents:tournament-leader-only", leaderName));

                    DropSide(m, side);
                    Announce(Lang.Get("canevents:tournament-forfeited", t.NameOfEntrant(side), t.Name));

                    var left = m.ActiveSides;
                    if (left.Count == 1) CloseMatch(t, m, left[0], "technical");
                    else if (left.Count == 0) CloseMatch(t, m, null, "doubleforfeit");
                    return (true, Lang.Get("canevents:tournament-forfeit-ok"));
                }

                if (!m.ReadyUids.Contains(player.PlayerUID)) m.ReadyUids.Add(player.PlayerUID);

                // Actual start happens on the tick: we hold the store lock here.
                if (m.ActiveSides.All(id => SideReady(t, m, id))) BeginStarting(t, m);

                return (true, Lang.Get("canevents:tournament-ready-ok"));
            });
        }

        // Popup plus chat fallback. The deadline is server-side only.
        private void SendReadyRequests(Tournament t, BracketMatch m)
        {
            foreach (var id in m.ActiveSides)
            {
                var e = t.FindEntrant(id);
                if (e?.Uids == null) continue;
                string opponent = OpponentsText(t, m, id);
                foreach (var uid in e.Uids)
                {
                    if (!(sapi.World.PlayerByUid(uid) is IServerPlayer player)) continue;
                    bool ready = m.ReadyUids.Contains(uid);
                    SendReadyPopup(t, m, player, opponent, alreadyReady: ready);
                    if (!ready)
                        player.SendMessage(0, Lang.Get("canevents:tournament-ready-prompt", t.Name, opponent,
                            Math.Max(0, m.ReadyDeadlineUnix - Now), t.Id), EnumChatType.Notification);
                }
            }
        }

        private void ResendReadyCheck(IServerPlayer player)
        {
            foreach (var t in store.ActiveSnapshot())
            {
                if (t.State != TournamentState.Running) continue;
                var m = CalledMatchOf(t, player.PlayerUID);
                if (m == null) continue;

                string side = SideOf(t, m, player.PlayerUID);
                string opponent = OpponentsText(t, m, side);
                SendReadyPopup(t, m, player, opponent, m.ReadyUids.Contains(player.PlayerUID));
                player.SendMessage(0, Lang.Get("canevents:tournament-ready-prompt", t.Name, opponent,
                    Math.Max(0, m.ReadyDeadlineUnix - Now), t.Id), EnumChatType.Notification);
                return;
            }
        }

        private void SendReadyPopup(Tournament t, BracketMatch m, IServerPlayer player,
            string opponent, bool alreadyReady)
        {
            manager.NetworkHandler?.SendTournamentReadyRequest(player, new Network.TournamentReadyRequestMsg
            {
                TournamentId = t.Id,
                MatchId = m.Id,
                TournamentName = t.Name,
                EventCode = m.EventCode,
                OpponentName = opponent,
                RoundLabel = BracketBuilder.RoundLabel(m, BracketBuilder.TotalRounds(t.Matches)),
                TimeoutSeconds = Math.Max(0, m.ReadyDeadlineUnix - Now),
                AlreadyReady = alreadyReady
            });
        }

        private void BeginStarting(Tournament t, BracketMatch m)
        {
            m.State = BracketMatchState.Starting;
            m.ReadyDeadlineUnix = 0;
            CloseReadyPopups(t, m, "starting");
            Tell(t, m, Lang.Get("canevents:tournament-ready-waiting"));
        }

        private void CloseReadyPopups(Tournament t, BracketMatch m, string reason)
        {
            var net = manager.NetworkHandler;
            if (net == null) return;
            foreach (var id in m.FilledSlots)
            {
                var e = t.FindEntrant(id);
                if (e?.Uids == null) continue;
                foreach (var uid in e.Uids)
                    if (sapi.World.PlayerByUid(uid) is IServerPlayer player)
                        net.SendTournamentReadyClosed(player, t.Id, m.Id, reason);
            }
        }

        private BracketMatch CalledMatchOf(Tournament t, string uid)
        {
            foreach (var m in t.Matches)
            {
                if (m.State != BracketMatchState.AwaitingReady) continue;
                if (SideOf(t, m, uid) != null) return m;
            }
            return null;
        }

        // First uid is the leader.
        private bool IsEntrantLeader(Tournament t, string entrantId, string uid, out string leaderName)
        {
            leaderName = "-";
            var e = t.FindEntrant(entrantId);
            if (e?.Uids == null || e.Uids.Count == 0) return false;
            leaderName = NameOf(e.Uids[0]);
            return e.Uids[0] == uid;
        }

        private string SideOf(Tournament t, BracketMatch m, string uid)
        {
            foreach (var id in m.FilledSlots)
            {
                var e = t.FindEntrant(id);
                if (e != null && e.Contains(uid)) return e.Id;
            }
            return null;
        }

        // Name stays in the slot so the walkover is still visible.
        private void DropSide(BracketMatch m, string entrantId)
        {
            if (string.IsNullOrEmpty(entrantId)) return;
            if (!m.AbsentSides.Contains(entrantId)) m.AbsentSides.Add(entrantId);
        }

        private string SidesText(Tournament t, BracketMatch m)
        {
            var names = m.FilledSlots.Select(t.NameOfEntrant).ToList();
            if (names.Count == 2) return names[0] + " vs " + names[1];
            if (names.Count <= 4) return string.Join(", ", names);
            return string.Join(", ", names.Take(3)) + " +" + (names.Count - 3);
        }

        private string OpponentsText(Tournament t, BracketMatch m, string ownSide)
        {
            var names = m.FilledSlots.Where(id => id != ownSide).Select(t.NameOfEntrant).ToList();
            if (names.Count == 0) return "-";
            if (names.Count <= 4) return string.Join(", ", names);
            return string.Join(", ", names.Take(3)) + " +" + (names.Count - 3);
        }

        // All members confirmed. Bots count as ready.
        private bool SideReady(Tournament t, BracketMatch m, string entrantId)
        {
            var e = t.FindEntrant(entrantId);
            if (e == null) return false;
            foreach (var uid in e.Uids)
                if (!Bots.BotIdentity.IsBot(uid) && !m.ReadyUids.Contains(uid)) return false;
            return true;
        }

        // Past the deadline whoever confirmed plays, no-shows are dropped.
        private void ExpireReadyChecks(Tournament t)
        {
            foreach (var m in t.Matches.ToList())
            {
                if (m.State != BracketMatchState.AwaitingReady) continue;
                if (m.ReadyDeadlineUnix == 0 || Now < m.ReadyDeadlineUnix) continue;

                var present = m.ActiveSides.Where(id => SideReady(t, m, id)).ToList();
                var missing = m.ActiveSides.Where(id => !present.Contains(id)).ToList();

                store.Mutate(_ =>
                {
                    foreach (var id in missing) DropSide(m, id);

                    if (present.Count == 0)
                    {
                        CloseMatch(t, m, null, "doubleforfeit");
                        Announce(Lang.Get("canevents:tournament-double-forfeit", t.Name, SidesText(t, m)));
                    }
                    else if (present.Count == 1)
                    {
                        CloseMatch(t, m, present[0], "technical");
                        Announce(Lang.Get("canevents:tournament-technical-loss",
                            t.NameOfEntrant(present[0]),
                            string.Join(", ", missing.Select(t.NameOfEntrant))));
                    }
                    else
                    {
                        BeginStarting(t, m);
                        if (missing.Count > 0)
                            Announce(Lang.Get("canevents:tournament-shorthanded", t.Name,
                                string.Join(", ", missing.Select(t.NameOfEntrant))));
                    }
                });
                PushUpdate(t.Id);
            }
        }
    }
}
