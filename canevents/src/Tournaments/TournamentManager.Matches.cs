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
        // A pair that can't go yet stays pending and is retried next tick.
        private void CallNextMatches(Tournament t)
        {
            // Manual mode: only the admin calls matches.
            if (t.ManualMatchStart) return;

            int live = t.Matches.Count(m => m.State == BracketMatchState.AwaitingReady
                                         || m.State == BracketMatchState.Starting
                                         || m.State == BracketMatchState.Live);
            if (live >= Math.Max(1, t.MaxConcurrentMatches)) return;

            var pending = CallableMatches(t);

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

            // One line per tournament, not per match, to keep the log readable.
            if (stalled > 0 && live == 0) LogStall(t, stalled, firstStall);
        }

        private static List<BracketMatch> CallableMatches(Tournament t) => t.Matches
            .Where(m => m.State == BracketMatchState.Pending && BracketBuilder.ReadyToPlay(t.Matches, m))
            .OrderBy(m => m.Round).ThenBy(m => m.SlotInRound)
            .ToList();

        // Also clears confirmations. Caller releases the called players.
        private static void ResetToPending(BracketMatch m)
        {
            m.State = BracketMatchState.Pending;
            m.RunningEventId = 0;
            m.ReadyDeadlineUnix = 0;
            m.StartDeadlineUnix = 0;
            m.ReadyUids.Clear();
        }

        // Runs without the store lock so store and running-events locks never nest in both orders.
        private void StartPendingStarts(Tournament t)
        {
            foreach (var m in t.Matches.ToList())
                if (m.State == BracketMatchState.Starting)
                    TryStartMatch(t, m);
        }

        // Ignores the parallel-match limit and reports why nothing could be called.
        public (bool ok, string message) ForceCallNext(long tournamentId) => Transition(tournamentId, t =>
        {
            if (t.State != TournamentState.Running) return (false, "the tournament is not running");

            var pending = CallableMatches(t);
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
            // Active sides only, don't wait for a no-show.
            foreach (var id in m.ActiveSides)
            {
                var e = t.FindEntrant(id);
                if (e == null) { why = "entrant missing"; return false; }
                foreach (var uid in e.Uids)
                {
                    // Ignore our own call mark (retries) and offline (the ready window handles that).
                    if (manager.PlayerState.Check(uid, ignoreTournamentCall: true) == PlayerAvailability.Busy)
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

        private string DescribeArenaProblem(Tournament t)
        {
            var parts = new List<string>();
            foreach (var code in t.EventCodes)
            {
                var ev = manager.FindEventByCode(code);
                if (ev == null) { parts.Add(code + ": no such event"); continue; }
                if (manager.DisabledEventCodes.Contains(ev.Code)) { parts.Add(code + ": event disabled"); continue; }

                // Same text as the admin panel's readiness column.
                parts.Add(code + ": " + manager.AvailabilityOf(ev, t.PlaceWhitelist).Summary);
            }
            return string.Join("; ", parts);
        }

        // Rotates event codes by round, first one with a free whitelisted arena wins.
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

        // From here both sides count as busy.
        private bool CallMatch(Tournament t, BracketMatch m)
        {
            var ev = PickEventFor(t, m);
            if (ev == null) return false;

            m.EventCode = ev.Code;
            // Keep readiness so a failed start doesn't make everyone confirm again.
            m.ReadyUids.RemoveAll(uid => SideOf(t, m, uid) == null);
            MarkCalled(t, m, true);

            int window = t.ReadyCheckSeconds;
            if (window <= 0)
            {
                // No ready window: the tick starts it (never from inside the store lock).
                if (!AnyoneOffline(t, m))
                {
                    m.State = BracketMatchState.Starting;
                    m.ReadyDeadlineUnix = 0;
                    store.Mutate(_ => { });
                    return true;
                }

                // Someone is offline: online players auto-confirm, absentees get a grace window.
                foreach (var id in m.ActiveSides)
                    foreach (var uid in t.FindEntrant(id)?.Uids ?? new List<string>())
                        if (manager.PlayerState.Check(uid, ignoreTournamentCall: true) != PlayerAvailability.Offline
                            && !m.ReadyUids.Contains(uid))
                            m.ReadyUids.Add(uid);
                window = OfflineGraceSeconds;
            }

            // Already confirmed (retry or bots): skip the window.
            if (m.ActiveSides.All(id => SideReady(t, m, id)))
            {
                BeginStarting(t, m);
                store.Mutate(_ => { });
                return true;
            }

            m.State = BracketMatchState.AwaitingReady;
            m.ReadyDeadlineUnix = Now + window;
            store.Mutate(_ => { });

            string label = BracketBuilder.RoundLabel(m, BracketBuilder.TotalRounds(t.Matches));
            Announce(Lang.Get("canevents:tournament-match-called", t.Name, label,
                SidesText(t, m), window));
            SendReadyRequests(t, m);
            PushUpdate(t.Id);
            return true;
        }

        // Grace for offline players when there's no ready check.
        private const int OfflineGraceSeconds = 60;

        // After this a match that keeps failing to start goes back to pending.
        private const int StartTimeoutSeconds = 300;

        private bool AnyoneOffline(Tournament t, BracketMatch m)
        {
            foreach (var id in m.ActiveSides)
                foreach (var uid in t.FindEntrant(id)?.Uids ?? new List<string>())
                    if (manager.PlayerState.Check(uid, ignoreTournamentCall: true) == PlayerAvailability.Offline)
                        return true;
            return false;
        }

        private bool TryStartMatch(Tournament t, BracketMatch m)
        {
            var ev = manager.FindEventByCode(m.EventCode) ?? PickEventFor(t, m);
            if (ev == null)
            {
                m.State = BracketMatchState.Pending;
                m.StartDeadlineUnix = 0;
                MarkCalled(t, m, false);
                return false;
            }

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

                // Pre-fill, otherwise the countdown prompt offers the mode's full list.
                if (t.AllowedLoadouts.Count > 0)
                    foreach (var uid in e.Uids)
                        if (!loadouts.ContainsKey(uid)) loadouts[uid] = t.AllowedLoadouts[0];
            }
            if (teams.Count < 2)
            {
                m.State = BracketMatchState.Pending;
                m.StartDeadlineUnix = 0;
                MarkCalled(t, m, false);
                return false;
            }

            // Bots need the ArenaBots behavior or nothing spawns. Works on a copy.
            bool hasBots = teams.Any(team => team.Any(Bots.BotIdentity.IsBot));
            if (hasBots) ev = Bots.BotMatchSupport.EnsureBotsCanSpawn(ev);

            // Set as an attribute on a copy, behaviors get rebuilt by the match.
            if (t.BringOwnGear) ev = TournamentEventTweaks.WithOwnGear(ev);

            var re = manager.StartMatchFor(ev, teams, loadouts, t.PlaceWhitelist, out string reason);
            if (re == null)
            {
                // Stay in Starting and retry until StartTimeoutSeconds, then back to pending.
                m.ReadyDeadlineUnix = 0;
                if (m.StartDeadlineUnix == 0) m.StartDeadlineUnix = Now + StartTimeoutSeconds;
                else if (Now >= m.StartDeadlineUnix)
                {
                    log.Warning("{0}: match #{1} could not start for {2}s ({3}), back to pending",
                        t.Id, m.Id, StartTimeoutSeconds, reason);
                    store.Mutate(_ =>
                    {
                        ResetToPending(m);
                        MarkCalled(t, m, false);
                    });
                    Tell(t, m, Lang.Get("canevents:tournament-start-timeout", t.Name));
                    PushUpdate(t.Id);
                    return false;
                }
                LogStall(t, 1, "match #" + m.Id + " cannot start - " + reason);
                store.Mutate(_ => { });
                return false;
            }

            // Fees and prizes are settled by the bracket at the end, not per round.
            re.SuppressEntryFeeSettlement = true;
            re.SuppressRating = t.RatedMode == 1;
            re.SuppressRewards = true;

            m.RunningEventId = re.Id;
            m.StartDeadlineUnix = 0;
            m.State = BracketMatchState.Live;
            store.Mutate(_ => { });
            PushUpdate(t.Id);

            Tell(t, m, Lang.Get("canevents:tournament-match-starting", m.EventCode));
            return true;
        }

        // Checked per pick packet, the client can send one after our pre-filled choice.
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

        // Keeps entrants still in the draw out of casual queues between rounds.
        public bool IsLockedEntrant(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return false;
            foreach (var t in store.ActiveSnapshot())
            {
                if (t.State != TournamentState.Running || !t.LockEntrantsWhileRunning) continue;
                var e = t.EntrantOfPlayer(uid);
                if (e == null) continue;
                if (t.Matches.Any(m => !m.IsClosed && m.FilledSlots.Contains(e.Id))) return true;
            }
            return false;
        }

        // Fires inside the match's lifecycle, so only record here; the tick calls the next pair.
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
                    // RunningEvent ids restart from 1 after a restart, so check state too.
                    if (t.State != TournamentState.Running) continue;
                    var m = t.Matches.FirstOrDefault(x => x.RunningEventId == re.Id);
                    if (m == null || m.IsClosed) continue;

                    ResetToPending(m);
                    MarkCalled(t, m, false);
                    log.Warning("{0}: match #{1} aborted ({2}), will retry", t.Id, m.Id, reason);
                    return;
                }
            });
        }

        // Null for nodes with more than two sides. Includes no-shows (walkover).
        private static string LoserOf(BracketMatch m, string winner)
        {
            var others = m.FilledSlots.Where(id => id != winner).ToList();
            return others.Count == 1 ? others[0] : null;
        }

        // Best first. Heats read the match ranking, so they need a mode that ranks.
        private List<string> MapWinners(Tournament t, BracketMatch m, MatchResult result)
        {
            var advancing = new List<string>();
            int wanted = Math.Max(1, m.AdvanceCount);
            // Disqualified sides may still be in the results.
            var sides = m.ActiveSides;

            if (result?.Ranking != null)
            {
                foreach (var bracket in result.Ranking)
                {
                    foreach (var id in sides)
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
                foreach (var id in sides)
                {
                    var e = t.FindEntrant(id);
                    if (e == null) continue;
                    if (e.Uids.Any(result.Winners.Contains)) { advancing.Add(id); break; }
                }
            }

            if (advancing.Count > wanted) advancing.RemoveRange(wanted, advancing.Count - wanted);
            return advancing;
        }

        // Null winner voids the node, the match above becomes a bye.
        private void CloseMatch(Tournament t, BracketMatch m, string winnerEntrantId, string kind)
            => CloseMatchAdvancing(t, m,
                winnerEntrantId == null ? new List<string>() : new List<string> { winnerEntrantId }, kind);

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

        private void Finalize(Tournament t)
        {
            var payouts = new List<PendingPayout>();
            Tournament next = null;

            // Single write so a crash can't leave a finished bracket with its pot gone.
            store.Mutate(d =>
            {
                t.FinalPlacement = ComputePlacement(t);
                for (int i = 0; i < t.FinalPlacement.Count; i++)
                {
                    var e = t.FindEntrant(t.FinalPlacement[i]);
                    if (e != null) e.PlacedAt = i + 1;
                }

                payouts = BuildPayouts(t);
                t.Pot.Clear();
                t.Contributions.Clear();
                d.Pending.AddRange(payouts);

                t.State = TournamentState.Finished;
                t.FinishedUnix = Now;

                // Queue the repeat now, not on archive, or short intervals lose a cycle.
                next = PrepareRepeat(t, d);
            });

            DeliverToOnline(payouts);

            string podium = string.Join(", ", t.FinalPlacement.Take(3)
                .Select((id, i) => (i + 1) + ". " + t.NameOfEntrant(id)));
            Announce(Lang.Get("canevents:tournament-finished", t.Name, podium));

            if (next != null)
            {
                Announce(Lang.Get("canevents:tournament-repeat-scheduled", next.Name,
                    Math.Max(1, (next.OpenAtUnix - Now + 59) / 60)));
                PushUpdate(next.Id);
            }

            ResumeSchedulerIfIdle();
            PushUpdate(t.Id);
            // Archived later by the tick (ArchiveFinished) so it doesn't vanish instantly.
        }

        private List<string> ComputePlacement(Tournament t)
        {
            var placement = new List<string>();
            int rounds = BracketBuilder.TotalRounds(t.Matches);

            var final = t.Matches.FirstOrDefault(m => m.IsFinal);
            if (final != null)
            {
                Add(final.WinnerEntrantId);
                // Null for a free-for-all final.
                Add(final.LoserEntrantId);
            }

            var third = t.Matches.FirstOrDefault(m => m.IsThirdPlace);
            if (third != null)
            {
                Add(third.WinnerEntrantId);
                Add(third.LoserEntrantId);
            }

            // Rest by elimination depth; never-eliminated first (cancelled brackets).
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
                // All qualifiers of a heat survived it, not just the first.
                if (m.WinnerEntrantIds.Contains(entrantId)) continue;
                if (m.Round > deepest) deepest = m.Round;
            }
            return deepest < 0 ? rounds : deepest;
        }
    }
}
