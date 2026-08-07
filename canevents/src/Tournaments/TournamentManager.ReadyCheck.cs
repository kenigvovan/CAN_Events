using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace canevents.src.Tournaments
{
    // The ready window: prompting the called sides, collecting confirmations, and deciding what a
    // missed deadline means.
    public partial class TournamentManager
    {
        /// <summary>A player confirms (or refuses) their called match. Validated against server state only.</summary>
        public (bool ok, string message) SetReady(IServerPlayer player, long tournamentId, int matchId, bool accept)
        {
            return Transition(tournamentId, t =>
            {
                if (t.State != TournamentState.Running) return (false, "tournament is not running");

                var m = matchId > 0 ? t.FindMatch(matchId) : CalledMatchOf(t, player.PlayerUID);
                if (m == null) return (false, Lang.Get("canevents:tournament-no-called-match"));

                // Already through the ready check and waiting on the server: say so instead of "you
                // have no match", which reads as if the click was lost.
                if (m.State == BracketMatchState.Starting || m.State == BracketMatchState.Live)
                    return (true, Lang.Get("canevents:tournament-ready-waiting"));

                if (m.State != BracketMatchState.AwaitingReady)
                    return (false, Lang.Get("canevents:tournament-no-called-match"));

                var side = SideOf(t, m, player.PlayerUID);
                if (side == null) return (false, Lang.Get("canevents:tournament-not-your-match"));

                if (!accept)
                {
                    // Without this, a repeated packet re-announced the forfeit server-wide each time.
                    if (m.AbsentSides.Contains(side))
                        return (false, Lang.Get("canevents:tournament-already-forfeited"));

                    // Giving up costs the whole side its match, so it is the leader's call.
                    if (!IsEntrantLeader(t, side, player.PlayerUID, out string leaderName))
                        return (false, Lang.Get("canevents:tournament-leader-only", leaderName));

                    // With more than two sides the rest still play; with two, the other walks through.
                    DropSide(m, side);
                    Announce(Lang.Get("canevents:tournament-forfeited", t.NameOfEntrant(side), t.Name));

                    var left = m.ActiveSides;
                    if (left.Count == 1) CloseMatch(t, m, left[0], "technical");
                    else if (left.Count == 0) CloseMatch(t, m, null, "doubleforfeit");
                    return (true, Lang.Get("canevents:tournament-forfeit-ok"));
                }

                if (!m.ReadyUids.Contains(player.PlayerUID)) m.ReadyUids.Add(player.PlayerUID);

                // Handing the match to CANEventManager happens on our own tick, never from here: we
                // hold the store lock and StartMatchFor takes the running-events lock.
                if (m.ActiveSides.All(id => SideReady(t, m, id))) BeginStarting(t, m);

                return (true, Lang.Get("canevents:tournament-ready-ok"));
            });
        }

        /// <summary>
        /// Tells both sides their match is up: popup packet plus a chat fallback. The deadline is the
        /// server's; the client countdown is decoration and never answers on its own.
        /// </summary>
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
                    SendReadyPopup(t, m, player, opponent, alreadyReady: false);
                    player.SendMessage(0, Lang.Get("canevents:tournament-ready-prompt", t.Name, opponent,
                        Math.Max(0, m.ReadyDeadlineUnix - Now), t.Id), EnumChatType.Notification);
                }
            }
        }

        /// <summary>Re-prompts a returning player whose ready window is still open.</summary>
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

        /// <summary>
        /// Everyone confirmed. The popup is closed rather than left saying "waiting for your opponent"
        /// while the server looks for an arena - untrue once the last side confirmed.
        /// </summary>
        private void BeginStarting(Tournament t, BracketMatch m)
        {
            m.State = BracketMatchState.Starting;
            m.ReadyDeadlineUnix = 0;
            CloseReadyPopups(t, m, "starting");
            Tell(t, m, Lang.Get("canevents:tournament-ready-waiting"));
        }

        /// <summary>Dismisses a ready popup that is no longer answerable, for everyone it was sent to.</summary>
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

        /// <summary>
        /// Is this player the one who speaks for the entrant? The first uid is the leader, and every
        /// decision binding the whole side is theirs alone. Solo entrants are their own leader.
        /// </summary>
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

        /// <summary>
        /// Marks a side as a no-show. The name stays in its slot (replacing it with "TBD" would lose
        /// the only record of what happened); it just does not play.
        /// </summary>
        private void DropSide(BracketMatch m, string entrantId)
        {
            if (string.IsNullOrEmpty(entrantId)) return;
            if (!m.AbsentSides.Contains(entrantId)) m.AbsentSides.Add(entrantId);
        }

        /// <summary>"A vs B" for a duel, "A, B, C and 5 more" for a free-for-all node.</summary>
        private string SidesText(Tournament t, BracketMatch m)
        {
            var names = m.FilledSlots.Select(t.NameOfEntrant).ToList();
            if (names.Count == 2) return names[0] + " vs " + names[1];
            if (names.Count <= 4) return string.Join(", ", names);
            return string.Join(", ", names.Take(3)) + " +" + (names.Count - 3);
        }

        /// <summary>The same list, minus the reader's own side.</summary>
        private string OpponentsText(Tournament t, BracketMatch m, string ownSide)
        {
            var names = m.FilledSlots.Where(id => id != ownSide).Select(t.NameOfEntrant).ToList();
            if (names.Count == 0) return "-";
            if (names.Count <= 4) return string.Join(", ", names);
            return string.Join(", ", names.Take(3)) + " +" + (names.Count - 3);
        }

        /// <summary>
        /// A team side is ready only when every member has confirmed. Bots cannot press anything, so a
        /// bot side is ready as soon as it is called - otherwise it would forfeit every deadline.
        /// </summary>
        private bool SideReady(Tournament t, BracketMatch m, string entrantId)
        {
            var e = t.FindEntrant(entrantId);
            if (e == null) return false;
            foreach (var uid in e.Uids)
                if (!Bots.BotIdentity.IsBot(uid) && !m.ReadyUids.Contains(uid)) return false;
            return true;
        }

        /// <summary>
        /// Closes ready windows whose deadline passed: whoever confirmed plays, no-shows are dropped.
        /// Generalizes "the one who turned up wins" to N-way nodes, where three of eight answering
        /// means a three-way match rather than a void round.
        /// </summary>
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
                        // Enough sides turned up for a real match - play it short-handed.
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
