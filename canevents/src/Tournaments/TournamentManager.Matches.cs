using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace canevents.src.Tournaments
{
    // Calling pairs, the ready window, and turning a finished match into bracket progress.
    public partial class TournamentManager
    {
        // calling the next pair

        /// <summary>
        /// Starts as many pending matches as the tournament allows at once, oldest round first. A pair
        /// that cannot go yet is left pending and retried next tick, never skipped permanently.
        /// </summary>
        private void CallNextMatches(Tournament t)
        {
            // Hand-paced tournament: only the admin's "call next match" reaches CallMatch.
            if (t.ManualMatchStart) return;

            int live = t.Matches.Count(m => m.State == BracketMatchState.AwaitingReady
                                         || m.State == BracketMatchState.Starting
                                         || m.State == BracketMatchState.Live);
            if (live >= Math.Max(1, t.MaxConcurrentMatches)) return;

            var pending = t.Matches
                .Where(m => m.State == BracketMatchState.Pending && m.IsPlayable)
                .OrderBy(m => m.Round).ThenBy(m => m.SlotInRound)
                .ToList();

            string firstStall = null;
            int stalled = 0;

            foreach (var m in pending)
            {
                if (live >= Math.Max(1, t.MaxConcurrentMatches)) break;
                if (!CanCall(t, m, out string why))
                {
                    stalled++;
                    firstStall ??= why;
                    continue;
                }
                if (CallMatch(t, m)) live++;
            }

            // One line per tournament, not per match: identical "no free arena" lines buried the log.
            if (stalled > 0 && live == 0) LogStall(t, stalled, firstStall);
        }

        /// <summary>
        /// Hands every match that finished its ready window to CANEventManager. Runs on our tick with
        /// no store lock held, so the store and running-events locks are never taken in both orders.
        /// </summary>
        private void StartPendingStarts(Tournament t)
        {
            foreach (var m in t.Matches.ToList())
                if (m.State == BracketMatchState.Starting)
                    TryStartMatch(t, m);
        }

        /// <summary>
        /// Calls the next waiting match now, ignoring the parallel-match limit. For when the tick will
        /// not and the admin needs to know why rather than watching the bracket sit still.
        /// </summary>
        public (bool ok, string message) ForceCallNext(long tournamentId) => Transition(tournamentId, t =>
        {
            if (t.State != TournamentState.Running) return (false, "the tournament is not running");

            var pending = t.Matches
                .Where(m => m.State == BracketMatchState.Pending && m.IsPlayable)
                .OrderBy(m => m.Round).ThenBy(m => m.SlotInRound)
                .ToList();
            if (pending.Count == 0) return (false, "no match is waiting to be called");

            var reasons = new List<string>();
            foreach (var m in pending)
            {
                if (!CanCall(t, m, out string why))
                {
                    reasons.Add("#" + m.Id + ": " + why);
                    continue;
                }
                if (CallMatch(t, m)) return (true, "called match #" + m.Id);
            }
            return (false, "nothing could be called - " + string.Join("; ", reasons.Take(3)));
        });

        private bool CanCall(Tournament t, BracketMatch m, out string why)
        {
            why = null;
            // Only the sides that will actually play: a no-show keeps its slot, but waiting for them
            // would stall the match they already forfeited.
            foreach (var id in m.ActiveSides)
            {
                var e = t.FindEntrant(id);
                if (e == null) { why = "entrant missing"; return false; }
                foreach (var uid in e.Uids)
                {
                    if (Bots.BotIdentity.IsBot(uid)) continue;   // a bot side is always available

                    if (!(sapi.World.PlayerByUid(uid) is IServerPlayer))
                    {
                        why = NameOf(uid) + " is offline";
                        return false;
                    }
                    // Only "busy elsewhere" counts: this match's own call already marked them called,
                    // so the general busy check would refuse every retry.
                    if (!IsCalled(uid) && manager.IsBusy(uid))
                    {
                        why = NameOf(uid) + " is in another match";
                        return false;
                    }
                }
            }

            if (PickEventFor(t, m) == null)
            {
                why = DescribeArenaProblem(t);
                return false;
            }
            return true;
        }

        /// <summary>
        /// Why no arena could be had - counted, not guessed, since a bare "no free arena" sends admins
        /// hunting for a missing place when one match is simply still holding the only one.
        /// </summary>
        private string DescribeArenaProblem(Tournament t)
        {
            var parts = new List<string>();
            foreach (var code in t.EventCodes)
            {
                var ev = manager.FindEventByCode(code);
                if (ev == null) { parts.Add(code + ": no such event"); continue; }
                if (manager.DisabledEventCodes.Contains(ev.Code)) { parts.Add(code + ": event disabled"); continue; }

                var tags = ev.EventTypes.ToArray();
                int tagged = 0, occupied = 0, disabled = 0, noSpawns = 0, outsideWhitelist = 0;
                foreach (var p in manager.eventPlacesManager.EventPlaces)
                {
                    if (p.placeTypes == null || !tags.All(tag => p.placeTypes.Contains(tag))) continue;
                    tagged++;
                    if (t.PlaceWhitelist.Count > 0 && !t.PlaceWhitelist.Contains(p.Id)) { outsideWhitelist++; continue; }
                    if (!p.Enabled) { disabled++; continue; }
                    if (p.spawnPositions == null || p.spawnPositions.Count == 0) { noSpawns++; continue; }
                    if (p.Occupied) occupied++;
                }

                if (tagged == 0) { parts.Add(code + ": no arena tagged " + string.Join("+", tags)); continue; }

                var detail = new List<string>();
                if (occupied > 0) detail.Add(occupied + " in use");
                if (outsideWhitelist > 0) detail.Add(outsideWhitelist + " outside this tournament's arena list");
                if (disabled > 0) detail.Add(disabled + " disabled");
                if (noSpawns > 0) detail.Add(noSpawns + " without spawn points");
                parts.Add(code + ": " + tagged + " matching arena(s), "
                    + (detail.Count == 0 ? "none free" : string.Join(", ", detail)));
            }
            return string.Join("; ", parts);
        }

        /// <summary>
        /// The event this match is played as: rotating the tournament's codes by round, among those
        /// with a free whitelisted arena right now.
        /// </summary>
        private EventManagers.events.CANEvent PickEventFor(Tournament t, BracketMatch m)
        {
            if (t.EventCodes.Count == 0) return null;

            int offset = m.Round % t.EventCodes.Count;
            for (int i = 0; i < t.EventCodes.Count; i++)
            {
                var code = t.EventCodes[(offset + i) % t.EventCodes.Count];
                var ev = manager.FindEventByCode(code);
                if (ev == null) continue;
                if (manager.DisabledEventCodes.Contains(ev.Code)) continue;
                if (manager.eventPlacesManager.TryFindEventPlace(ev.EventTypes.ToArray(), out _, t.PlaceWhitelist))
                    return ev;
            }
            return null;
        }

        private void LogStall(Tournament t, int stalledCount, string why)
        {
            long now = Now;
            int key = (int)t.Id;
            if (lastStallLog.TryGetValue(key, out long last) && now - last < 60) return;
            lastStallLog[key] = now;
            log.Notification("{0}: {1} match(es) waiting - {2}", t.Id, stalledCount, why);
        }

        /// <summary>
        /// Opens the ready window for a pair (or starts immediately when the tournament runs without
        /// one). From this moment both sides count as busy, so nothing else can claim them.
        /// </summary>
        private bool CallMatch(Tournament t, BracketMatch m)
        {
            var ev = PickEventFor(t, m);
            if (ev == null) return false;

            m.EventCode = ev.Code;
            // Readiness is NOT cleared here: a confirmed match that failed to start comes back through
            // this path, and wiping the flags made everyone click again. Only ex-players are dropped.
            m.ReadyUids.RemoveAll(uid => SideOf(t, m, uid) == null);
            MarkCalled(t, m, true);

            // No ready window: go straight to Starting and let the tick pick it up. Starting is never
            // done from inside the store lock - see StartPendingStarts.
            if (t.ReadyCheckSeconds <= 0)
            {
                m.State = BracketMatchState.Starting;
                m.ReadyDeadlineUnix = 0;
                store.Mutate(_ => { });
                return true;
            }

            // Everyone already confirmed (a retry after a failed start, or a side made of bots):
            // don't open a fresh window and don't ask again.
            if (m.ActiveSides.All(id => SideReady(t, m, id)))
            {
                BeginStarting(t, m);
                store.Mutate(_ => { });
                return true;
            }

            m.State = BracketMatchState.AwaitingReady;
            m.ReadyDeadlineUnix = Now + t.ReadyCheckSeconds;
            store.Mutate(_ => { });

            string label = BracketBuilder.RoundLabel(m, BracketBuilder.TotalRounds(t.Matches));
            Announce(Lang.Get("canevents:tournament-match-called", t.Name, label,
                SidesText(t, m), t.ReadyCheckSeconds));
            SendReadyRequests(t, m);
            PushUpdate(t.Id);
            return true;
        }

        // starting the match (the ready window itself lives in TournamentManager.ReadyCheck.cs)

        private bool TryStartMatch(Tournament t, BracketMatch m)
        {
            var ev = manager.FindEventByCode(m.EventCode) ?? PickEventFor(t, m);
            if (ev == null)
            {
                m.State = BracketMatchState.Pending;
                MarkCalled(t, m, false);
                return false;
            }

            // One team per filled slot: two for a duel, as many as the node seats for a free-for-all.
            var teams = new List<IReadOnlyList<string>>();
            var loadouts = new Dictionary<string, string>();
            foreach (var id in m.ActiveSides)
            {
                var e = t.FindEntrant(id);
                if (e?.Uids == null || e.Uids.Count == 0) continue;
                teams.Add(e.Uids);
                foreach (var kv in e.LoadoutChoices)
                    if (e.Contains(kv.Key) && !string.IsNullOrEmpty(kv.Value) && t.AllowsLoadout(kv.Value))
                        loadouts[kv.Key] = kv.Value;

                // With a restricted class list, pre-fill everyone: the countdown prompt would offer the
                // mode's full list, which is exactly what the restriction forbids.
                if (t.AllowedLoadouts.Count > 0)
                    foreach (var uid in e.Uids)
                        if (!loadouts.ContainsKey(uid)) loadouts[uid] = t.AllowedLoadouts[0];
            }
            if (teams.Count < 2)
            {
                m.State = BracketMatchState.Pending;
                MarkCalled(t, m, false);
                return false;
            }

            // A bot side needs the ArenaBots behavior, or nothing spawns, nobody can die and the match
            // holds its arena until the time limit. Only the copy is touched.
            bool hasBots = teams.Any(team => team.Any(Bots.BotIdentity.IsBot));
            if (hasBots) ev = Bots.BotMatchSupport.EnsureBotsCanSpawn(ev);

            // Bring-your-own belongs to this tournament, not the mode, so it goes onto a copy as an
            // attribute - the only thing that survives the match rebuilding its behaviors.
            if (t.BringOwnGear) ev = TournamentEventTweaks.WithOwnGear(ev);

            var re = manager.StartMatchFor(ev, teams, loadouts, t.PlaceWhitelist, out string reason);
            if (re == null)
            {
                // Stay in Starting and retry on the tick: dropping back to Pending re-ran the ready
                // check and threw away confirmations. The cleared deadline keeps ExpireReadyChecks off.
                m.ReadyDeadlineUnix = 0;
                LogStall(t, 1, "match #" + m.Id + " cannot start - " + reason);
                store.Mutate(_ => { });
                return false;
            }

            // The bracket owns the stakes, not the sub-match: nothing is settled per round.
            re.SuppressEntryFeeSettlement = true;
            re.SuppressRating = t.RatedMode == 1;
            // Prizes are the bracket's, paid once at the end; the event's own reward would otherwise
            // be paid again for every round survived.
            re.SuppressRewards = true;

            m.RunningEventId = re.Id;
            m.RunningEventIds.Add(re.Id);
            m.State = BracketMatchState.Live;
            store.Mutate(_ => { });
            PushUpdate(t.Id);

            Tell(t, m, Lang.Get("canevents:tournament-match-starting", m.EventCode));
            return true;
        }

        /// <summary>
        /// May this player switch to that loadout set in this running match? Only a bracket that
        /// restricts or locks classes says no. Checked on every pick packet, since an unsolicited one
        /// arrives after TryStartMatch's pre-filled choices.
        /// </summary>
        public bool AllowsLoadoutChange(long runningEventId, string uid, string setName)
        {
            foreach (var t in store.ActiveSnapshot())
            {
                if (t.State != TournamentState.Running) continue;
                var m = t.Matches.FirstOrDefault(x => x.RunningEventId != 0 && x.RunningEventId == runningEventId);
                if (m == null) continue;
                if (t.LockLoadout) return false;
                return t.AllowsLoadout(setName);
            }
            return true;
        }

        /// <summary>
        /// Is this player held by a running bracket that locks its entrants? Without this an entrant
        /// could queue for casual matches between rounds and stall the tournament. Only sides still in
        /// the draw are held.
        /// </summary>
        public bool IsLockedEntrant(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return false;
            foreach (var t in store.ActiveSnapshot())
            {
                if (t.State != TournamentState.Running || !t.LockEntrantsWhileRunning) continue;
                var e = t.EntrantOfPlayer(uid);
                if (e == null) continue;
                // Still in the draw = has at least one match that has not been played out yet.
                if (t.Matches.Any(m => !m.IsClosed && m.FilledSlots.Contains(e.Id))) return true;
            }
            return false;
        }

        // results

        /// <summary>
        /// Match-finished observer; only records the outcome. Advancing the bracket and calling the
        /// next pair happen on our tick, because this fires inside the finishing match's lifecycle.
        /// </summary>
        private void HandleMatchFinished(RunningEvent re, MatchResult result)
        {
            long touched = 0;
            store.Mutate(d =>
            {
                foreach (var t in d.Active)
                {
                    if (t.State != TournamentState.Running) continue;
                    var m = t.Matches.FirstOrDefault(x => x.RunningEventId == re.Id);
                    if (m == null || m.IsClosed) continue;

                    var advancing = MapWinners(t, m, result);
                    string winner = advancing.Count > 0 ? advancing[0] : null;
                    var beaten = m.FilledSlots.Where(id => !advancing.Contains(id)).ToList();
                    CloseMatchAdvancing(t, m, advancing, winner != null ? "played" : "void");
                    touched = t.Id;

                    if (winner != null)
                        Announce(Lang.Get("canevents:tournament-match-result", t.Name,
                            string.Join(", ", advancing.Select(t.NameOfEntrant)),
                            beaten.Count == 0 ? "-" : string.Join(", ", beaten.Select(t.NameOfEntrant))));
                    else
                        Announce(Lang.Get("canevents:tournament-match-draw", t.Name));
                    return;
                }
            });
            if (touched != 0) PushUpdate(touched);
        }

        private void HandleMatchAborted(RunningEvent re, string reason)
        {
            store.Mutate(d =>
            {
                foreach (var t in d.Active)
                {
                    // Running only: RunningEvent ids restart from 1 after a server restart, so a stale
                    // Live node could otherwise swallow an abort belonging to a real match.
                    if (t.State != TournamentState.Running) continue;
                    var m = t.Matches.FirstOrDefault(x => x.RunningEventId == re.Id);
                    if (m == null || m.IsClosed) continue;

                    // Nothing was played: back to Pending so the tick retries once an arena frees up.
                    m.State = BracketMatchState.Pending;
                    m.RunningEventId = 0;
                    m.ReadyUids.Clear();
                    m.ReadyDeadlineUnix = 0;
                    MarkCalled(t, m, false);
                    log.Warning("{0}: match #{1} aborted ({2}), will retry", t.Id, m.Id, reason);
                    return;
                }
            });
        }

        /// <summary>The single side that loses a duel, or null when the node had more than two.</summary>
        private static string LoserOf(BracketMatch m, string winner)
        {
            // Counts the no-show, who is the loser in a walkover.
            var others = m.FilledSlots.Where(id => id != winner).ToList();
            return others.Count == 1 ? others[0] : null;
        }

        /// <summary>
        /// Who comes out of this match, best first: the winner for a knockout, or as many as a heat
        /// advances, read off the match's ranking - which is why heats need a mode that ranks.
        /// </summary>
        private List<string> MapWinners(Tournament t, BracketMatch m, MatchResult result)
        {
            var advancing = new List<string>();
            int wanted = Math.Max(1, m.AdvanceCount);

            // Ranked brackets first: they place everyone, not just the winner.
            if (result?.Ranking != null)
            {
                foreach (var bracket in result.Ranking)
                {
                    foreach (var id in m.FilledSlots)
                    {
                        if (advancing.Contains(id)) continue;
                        var e = t.FindEntrant(id);
                        if (e == null) continue;
                        if (e.Uids.Any(bracket.Contains)) advancing.Add(id);
                    }
                    if (advancing.Count >= wanted) break;
                }
            }

            if (advancing.Count == 0 && result?.Winners != null && result.Winners.Count > 0)
            {
                foreach (var id in m.FilledSlots)
                {
                    var e = t.FindEntrant(id);
                    if (e == null) continue;
                    if (e.Uids.Any(result.Winners.Contains)) { advancing.Add(id); break; }
                }
            }

            if (advancing.Count > wanted) advancing.RemoveRange(wanted, advancing.Count - wanted);
            return advancing;
        }

        /// <summary>
        /// Records a decision and pushes it through the bracket. A null winner voids the node: everyone
        /// is out and the match above collapses into a bye for whoever is left.
        /// </summary>
        private void CloseMatch(Tournament t, BracketMatch m, string winnerEntrantId, string kind)
            => CloseMatchAdvancing(t, m,
                winnerEntrantId == null ? new List<string>() : new List<string> { winnerEntrantId }, kind);

        /// <summary>Closes a match where more than one side may go through (a qualifying heat).</summary>
        private void CloseMatchAdvancing(Tournament t, BracketMatch m, List<string> winners, string kind)
        {
            if (m.State == BracketMatchState.AwaitingReady) CloseReadyPopups(t, m, kind);

            var sides = m.FilledSlots;
            winners = winners ?? new List<string>();
            string winnerEntrantId = winners.Count > 0 ? winners[0] : null;

            m.WinnerEntrantIds = new List<string>(winners);
            m.LoserEntrantId = winnerEntrantId == null ? null : LoserOf(m, winnerEntrantId);
            m.ResultKind = kind;
            m.State = winnerEntrantId == null ? BracketMatchState.Void : BracketMatchState.Done;
            m.FinishedUnix = Now;
            m.RunningEventId = 0;
            MarkCalled(t, m, false);

            // Everyone who did not come out of this node is out of the tournament.
            foreach (var id in sides)
                if (!winners.Contains(id)) MarkEliminated(t, id);

            BracketBuilder.Advance(t.Matches, m);
            BracketBuilder.PropagateByes(t.Matches);
        }

        private void MarkEliminated(Tournament t, string entrantId)
        {
            var e = t.FindEntrant(entrantId);
            if (e != null) e.Eliminated = true;
        }

        // finish

        /// <summary>
        /// Every match is decided: work out the standings, pay out and announce. Placement is read off
        /// the bracket, with everyone outside the podium ordered by how deep they got.
        /// </summary>
        private void Finalize(Tournament t)
        {
            store.Mutate(_ =>
            {
                t.FinalPlacement = ComputePlacement(t);
                for (int i = 0; i < t.FinalPlacement.Count; i++)
                {
                    var e = t.FindEntrant(t.FinalPlacement[i]);
                    if (e != null) e.PlacedAt = i + 1;
                }
                t.State = TournamentState.Finished;
                t.FinishedUnix = Now;
            });

            PayOut(t);

            string podium = string.Join(", ", t.FinalPlacement.Take(3)
                .Select((id, i) => (i + 1) + ". " + t.NameOfEntrant(id)));
            Announce(Lang.Get("canevents:tournament-finished", t.Name, podium));

            ResumeSchedulerIfIdle();
            PushUpdate(t.Id);
            // Deliberately NOT archived here: vanishing from the list the instant the last match ends
            // looks like a deletion. The tick archives it later (see ArchiveFinished).
        }

        private List<string> ComputePlacement(Tournament t)
        {
            var placement = new List<string>();
            int rounds = BracketBuilder.TotalRounds(t.Matches);

            var final = t.Matches.FirstOrDefault(m => m.IsFinal);
            if (final != null)
            {
                Add(final.WinnerEntrantId);
                // A single runner-up only exists in a duel final; a free-for-all's losers are ordered
                // further down by how deep they got.
                Add(final.LoserEntrantId);
            }

            var third = t.Matches.FirstOrDefault(m => m.IsThirdPlace);
            if (third != null)
            {
                Add(third.WinnerEntrantId);
                Add(third.LoserEntrantId);
            }

            // Everyone else, deepest elimination first; entrants who never lost come before those
            // who did (a bracket cut short by cancellation).
            var rest = t.Entrants
                .Where(e => !placement.Contains(e.Id))
                .OrderByDescending(e => EliminationRound(t, e.Id, rounds))
                .ToList();
            foreach (var e in rest) Add(e.Id);

            return placement;

            void Add(string id)
            {
                if (!string.IsNullOrEmpty(id) && !placement.Contains(id)) placement.Add(id);
            }
        }

        private int EliminationRound(Tournament t, string entrantId, int rounds)
        {
            int deepest = -1;
            foreach (var m in t.Matches)
            {
                if (!m.IsClosed) continue;
                if (!m.Contains(entrantId)) continue;
                // Everyone who came OUT of this node survived it, not just the top name: checking only
                // the winner ranked a heat's other qualifiers as knocked out in the round they won.
                if (m.WinnerEntrantIds.Contains(entrantId)) continue;
                if (m.Round > deepest) deepest = m.Round;
            }
            return deepest < 0 ? rounds : deepest;
        }
    }
}
