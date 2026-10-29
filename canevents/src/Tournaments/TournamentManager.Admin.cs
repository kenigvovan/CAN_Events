using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers;
using canevents.src.EventManagers.eventBehavior.Shared;
using canevents.src.EventManagers.events;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace canevents.src.Tournaments
{
    public partial class TournamentManager
    {
        public (bool ok, string message) Create(string name, IEnumerable<string> eventCodes, int teamSize)
        {
            var codes = (eventCodes ?? Enumerable.Empty<string>())
                .Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).ToList();
            if (codes.Count == 0) return (false, "no event code given");

            foreach (var code in codes)
            {
                var ev = manager.FindEventByCode(code);
                if (ev == null) return (false, "unknown event code: " + code);
            }

            var t = new Tournament
            {
                Id = store.AllocateId(),
                Name = string.IsNullOrWhiteSpace(name) ? "Tournament" : name.Trim(),
                EventCodes = codes,
                // Bounded: team size drives per-entrant roster allocation.
                TeamSize = Math.Clamp(teamSize, 1, 16),
                CreatedUnix = Now
            };
            store.Mutate(d => d.Active.Add(t));
            PushUpdate(t.Id);
            return (true, "created tournament #" + t.Id + " '" + t.Name + "'");
        }

        // Only before the bracket exists and before anyone paid.
        public (bool ok, string message) Configure(long id, System.Func<Tournament, (bool, string)> edit)
            => Transition(id, t =>
            {
                if (t.State != TournamentState.Draft && t.State != TournamentState.Signup
                    && t.State != TournamentState.Seeded)
                    return (false, "a running or finished tournament can no longer be configured");
                if (t.State != TournamentState.Draft && t.Contributions.Count > 0)
                    return (false, "entrants have already paid - cancel with refund first");
                return edit(t);
            });

        public (bool ok, string message) OpenSignup(long id) => Transition(id, t =>
        {
            if (t.State != TournamentState.Draft) return (false, "signup can only open from draft");
            if (t.EventCodes.Count == 0) return (false, "no event configured");
            t.State = TournamentState.Signup;
            t.OpenAtUnix = 0;
            if (t.SignupWindowMinutes > 0) t.SignupDeadlineUnix = Now + t.SignupWindowMinutes * 60L;
            Announce(Lang.Get("canevents:tournament-signup-open", t.Name, string.Join(", ", t.EventCodes)));
            return (true, "signup open");
        });

        public (bool ok, string message) CloseSignup(long id) => Transition(id, t =>
        {
            if (t.State != TournamentState.Signup) return (false, "signup is not open");
            if (t.Entrants.Count < 2) return (false, "need at least two entrants");
            t.State = TournamentState.Seeded;
            return (true, "signup closed with " + t.Entrants.Count + " entrant(s)");
        });

        public (bool ok, string message) Begin(long id) => Transition(id, t =>
        {
            if (t.State == TournamentState.Signup && t.Entrants.Count >= 2) t.State = TournamentState.Seeded;
            if (t.State != TournamentState.Seeded) return (false, "tournament is not ready to begin");
            if (t.Entrants.Count < 2) return (false, "need at least two entrants");

            // Fail now instead of stalling later on a disabled event.
            var off = t.EventCodes.Where(c => manager.DisabledEventCodes.Contains(
                manager.FindEventByCode(c)?.Code ?? c)).ToList();
            if (off.Count > 0)
                return (false, "these events are switched off: " + string.Join(", ", off)
                    + " - enable them on the Events tab first");

            var seeded = SeedEntrants(t);
            int arity = ResolveArity(t);
            t.Matches = BracketBuilder.Build(seeded, t.ThirdPlaceMatch, arity, t.AdvancePerMatch);
            if (t.Matches.Count == 0) return (false, "could not build a bracket");

            t.State = TournamentState.Running;
            t.StartedUnix = Now;

            if (t.PauseScheduler && scheduler != null && scheduler.Enabled)
            {
                scheduler.SetEnabled(false);
                schedulerPausedByUs = true;
            }

            Announce(Lang.Get("canevents:tournament-begun", t.Name, t.Entrants.Count));
            return (true, "bracket built: " + t.Matches.Count + " match(es)");
        });

        public (bool ok, string message) Cancel(long id, bool refund) => Transition(id, t =>
        {
            if (t.State == TournamentState.Finished || t.State == TournamentState.Cancelled)
                return (false, "already over");

            foreach (var m in t.Matches)
            {
                StopLiveMatch(m, "tournament cancelled");
                // RunningEvent ids can repeat after a crash.
                m.RunningEventId = 0;
                m.ReadyDeadlineUnix = 0;
                m.StartDeadlineUnix = 0;
                MarkCalled(t, m, false);
            }

            if (refund) RefundAllContributions(t);
            else
            {
                // Clear so a later Delete doesn't refund them.
                t.Contributions.Clear();
                t.Pot.Clear();
            }

            t.State = TournamentState.Cancelled;
            t.FinishedUnix = Now;
            ResumeSchedulerIfIdle();
            Announce(Lang.Get("canevents:tournament-cancelled", t.Name));
            return (true, "cancelled");
        });

        // No history row. Always refunds.
        public (bool ok, string message) Delete(long id)
        {
            Tournament target = null;
            var result = store.MutateChecked(d =>
            {
                target = d.Active.FirstOrDefault(x => x.Id == id);
                if (target == null) return (false, "no tournament #" + id);
                if (target.State == TournamentState.Running)
                    return (false, "cancel it first - it is still running");

                foreach (var m in target.Matches) MarkCalled(target, m, false);
                RefundAllContributions(target);
                d.Active.Remove(target);
                return (true, "deleted tournament #" + id);
            });

            if (result.ok)
            {
                ResumeSchedulerIfIdle();
                PushUpdate(id);
            }
            return result;
        }

        public (bool ok, string message) SetMatchResult(long id, int matchId, string sideOrEntrantId) => Transition(id, t =>
        {
            var m = t.FindMatch(matchId);
            if (m == null) return (false, "no match #" + matchId);
            if (m.IsClosed) return (false, "match #" + matchId + " is already decided");
            if (!BracketBuilder.ReadyToPlay(t.Matches, m))
                return (false, "match #" + matchId + " is still waiting for its sides - decide the earlier "
                    + "match first, or disqualify");

            string winner = ResolveSide(t, m, sideOrEntrantId);
            if (winner == null) return (false, "cannot tell which side you mean: " + sideOrEntrantId);
            if (m.IsAbsent(winner)) return (false, t.NameOfEntrant(winner) + " is no longer in this match");

            StopLiveMatch(m, "tournament admin override");
            CloseMatch(t, m, winner, "admin");
            return (true, "match #" + matchId + " awarded to " + t.NameOfEntrant(winner));
        });

        // Testing. Bots pay no fee, get no prize, no Elo.
        public (bool ok, string message) AddBots(long id, int count) => Transition(id, t =>
        {
            if (t.State != TournamentState.Draft && t.State != TournamentState.Signup
                && t.State != TournamentState.Seeded)
                return (false, "the bracket has already started");
            if (count <= 0) return (false, "how many?");
            if (t.EntryFee != null && t.EntryFee.Count > 0)
                return (false, "clear the entry fee first - a bot cannot pay one");

            int added = 0;
            while (added < count && t.Entrants.Count < t.MaxEntrants)
            {
                var uids = new List<string>();
                for (int i = 0; i < Math.Max(1, t.TeamSize); i++)
                    uids.Add(Bots.BotIdentity.NewUid(t.Entrants.Count * 10 + i));

                // Name from uid so the bracket and the arena agree.
                t.Entrants.Add(new Entrant
                {
                    Id = Guid.NewGuid().ToString(),
                    Uids = uids,
                    DisplayName = uids.Count == 1
                        ? Bots.BotIdentity.NameOfUid(uids[0])
                        : Bots.BotIdentity.NameOfUid(uids[0]) + " +" + (uids.Count - 1),
                    MembersText = string.Join(", ", uids.Select(Bots.BotIdentity.NameOfUid))
                });
                added++;
            }
            return (true, "added " + added + " bot entrant(s), field is now " + t.Entrants.Count);
        });

        public (bool ok, string message) Disqualify(long id, string entrantIdOrName) => Transition(id, t =>
        {
            var e = FindEntrantLoose(t, entrantIdOrName);
            if (e == null) return (false, "no such entrant: " + entrantIdOrName);

            e.Eliminated = true;
            foreach (var m in t.Matches)
            {
                if (m.IsClosed) continue;

                // Pre-mark the 3rd place match so a walkover semi doesn't route them there.
                if (!m.Contains(e.Id))
                {
                    if (m.IsThirdPlace) DropSide(m, e.Id);
                    continue;
                }

                DropSide(m, e.Id);

                // PropagateByes settles it once the feeders are done.
                if (BracketBuilder.AwaitingFeeders(t.Matches, m)) continue;

                var left = m.ActiveSides;
                if (left.Count <= 1) StopLiveMatch(m, "tournament disqualification");
                if (left.Count == 0) CloseMatch(t, m, null, "void");
                else if (left.Count == 1) CloseMatch(t, m, left[0], "technical");
            }
            BracketBuilder.PropagateByes(t.Matches);

            Announce(Lang.Get("canevents:tournament-disqualified", e.DisplayName, t.Name));
            return (true, e.DisplayName + " disqualified");
        });
    }
}
