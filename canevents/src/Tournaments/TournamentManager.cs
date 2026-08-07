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
    /// <summary>
    /// Drives tournaments on top of the ordinary match machinery - not an event type, not a queue
    /// manager. It owns a persistent bracket and uses two seams: StartMatchFor to run a match with an
    /// exact roster, and OnMatchFinished/OnMatchAborted to hear how it ended; everything else is the
    /// normal path. All state lives in TournamentStore. Threading: the seam events fire from a
    /// finishing match's tick and only write to the store; matches are started exclusively on this
    /// class's own 1 Hz tick, so CANEventManager is never re-entered from inside its own lifecycle.
    /// </summary>
    public partial class TournamentManager : IDisposable
    {
        private readonly ICoreServerAPI sapi;
        private readonly CANEventManager manager;
        private readonly TournamentStore store;
        private readonly EventScheduler scheduler;
        private readonly Random rng = new Random();

        private long tickListenerId = -1;

        /// <summary>Everything this driver writes to the log goes out under one tag.</summary>
        private Log log;

        /// <summary>
        /// Uids whose bracket match has been called. Read by IsPlayerBusy so the scheduler and queue
        /// cannot pull them away mid-call. In memory only - rebuilt by <see cref="RecoverAfterRestart"/>.
        /// </summary>
        private readonly HashSet<string> calledUids = new HashSet<string>();

        /// <summary>Throttles the "can't call this pair yet" log so a stalled bracket doesn't spam.</summary>
        private readonly Dictionary<int, long> lastStallLog = new Dictionary<int, long>();

        private bool schedulerPausedByUs;

        public TournamentManager(ICoreServerAPI sapi, CANEventManager manager, TournamentStore store,
            EventScheduler scheduler)
        {
            this.sapi = sapi;
            this.manager = manager;
            this.store = store;
            this.scheduler = scheduler;
            log = Log.For(sapi?.World?.Logger, "tournament");
        }

        /// <summary>The shared clock, kept as a shorthand because this file reads it constantly.</summary>
        public static long Now => UnixTime.Now;

        public void Start()
        {
            manager.OnMatchFinished += HandleMatchFinished;
            manager.OnMatchAborted += HandleMatchAborted;
            tickListenerId = sapi.Event.RegisterGameTickListener(OnTick, 1000);
        }

        public void Dispose()
        {
            manager.OnMatchFinished -= HandleMatchFinished;
            manager.OnMatchAborted -= HandleMatchAborted;
            if (tickListenerId >= 0) sapi.Event.UnregisterGameTickListener(tickListenerId);
            tickListenerId = -1;
        }

        /// <summary>True while this player's bracket match is called - see <see cref="calledUids"/>.</summary>
        public bool IsCalled(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return false;
            lock (calledUids) return calledUids.Contains(uid);
        }

        private void MarkCalled(Tournament t, BracketMatch m, bool called)
        {
            lock (calledUids)
            {
                // Only the sides still in it: a player who forfeited is free to do anything else.
                foreach (var id in called ? m.ActiveSides : m.FilledSlots)
                {
                    var e = t.FindEntrant(id);
                    if (e?.Uids == null) continue;
                    foreach (var uid in e.Uids)
                    {
                        if (called) calledUids.Add(uid);
                        else calledUids.Remove(uid);
                    }
                }
            }
        }

        // tick

        private void OnTick(float dt)
        {
            var active = store.ActiveSnapshot();
            foreach (var t in active)
            {
                // One tournament throwing must not stop the others from being driven.
                log.Safely("tournament " + t.Id + " tick", () =>
                {
                    switch (t.State)
                    {
                        case TournamentState.Draft:
                            // A scheduled tournament opens itself - nobody has to be online to press
                            // a button, which is the point of scheduling one.
                            if (t.OpenAtUnix > 0 && Now >= t.OpenAtUnix)
                            {
                                var opened = OpenSignup(t.Id);
                                if (!opened.ok)
                                    log.Warning("tournament {0}: scheduled open failed - {1}", t.Id, opened.message);
                            }
                            break;

                        case TournamentState.Signup:
                            if (CheckAutoStart(t) || (t.SignupDeadlineUnix > 0 && Now >= t.SignupDeadlineUnix))
                            {
                                var closed = CloseSignup(t.Id);
                                if (closed.ok) Begin(t.Id);
                                else Announce(Lang.Get("canevents:tournament-autostart-failed", t.Name, closed.message));
                            }
                            break;

                        case TournamentState.Seeded:
                            // Seeded is transient (CloseSignup -> Begin on the same tick). A withdrawal
                            // in the gap can fail Begin and strand it here, so reopen signup rather
                            // than wedge forever.
                            if (t.Entrants.Count < 2)
                            {
                                store.Mutate(_ => t.State = TournamentState.Signup);
                                Announce(Lang.Get("canevents:tournament-autostart-failed", t.Name,
                                    "not enough entrants"));
                            }
                            break;

                        case TournamentState.Running:
                            ExpireReadyChecks(t);
                            StartPendingStarts(t);
                            CallNextMatches(t);
                            if (AllMatchesClosed(t)) Finalize(t);
                            break;

                        case TournamentState.Finished:
                        case TournamentState.Cancelled:
                            ArchiveFinished(t);
                            break;
                    }
                });
            }
        }

        /// <summary>
        /// Arms (or disarms) the "enough players, start it" countdown and reports when it fires. It is
        /// cancelled if someone withdraws, since the field is no longer complete.
        /// </summary>
        private bool CheckAutoStart(Tournament t)
        {
            if (t.AutoStartEntrants <= 0) return false;

            if (t.Entrants.Count < t.AutoStartEntrants)
            {
                if (t.AutoStartAtUnix != 0)
                {
                    store.Mutate(_ => t.AutoStartAtUnix = 0);
                    Announce(Lang.Get("canevents:tournament-autostart-cancelled", t.Name));
                }
                return false;
            }

            if (t.AutoStartAtUnix == 0)
            {
                int delay = Math.Max(0, t.AutoStartDelaySeconds);
                store.Mutate(_ => t.AutoStartAtUnix = Now + delay);
                if (delay > 0)
                    Announce(Lang.Get("canevents:tournament-autostart-armed", t.Name, t.Entrants.Count, delay));
            }

            return Now >= t.AutoStartAtUnix;
        }

        /// <summary>
        /// How long a finished or cancelled tournament stays in the list before moving to the history.
        /// </summary>
        private const int ArchiveAfterSeconds = 15 * 60;

        private void ArchiveFinished(Tournament t)
        {
            if (t.FinishedUnix == 0)
            {
                store.Mutate(_ => t.FinishedUnix = Now);
                // The successor is queued when this one ends, not when it is archived, so a short
                // repeat interval does not lose its cycle to the reading period. Only a tournament
                // that RAN comes back: cancelling is how an admin stops a series.
                if (t.State == TournamentState.Finished) ScheduleRepeat(t);
                return;
            }
            if (Now - t.FinishedUnix < ArchiveAfterSeconds) return;

            store.Archive(t);
            PushUpdate(t.Id);
        }

        /// <summary>
        /// Queues the next run of a repeating tournament as a fresh Draft. Only the configuration is
        /// copied - entrants, bracket, pot and results stay with the run that produced them.
        /// </summary>
        private void ScheduleRepeat(Tournament t)
        {
            if (!t.Repeats) return;

            long openAt = NextOpeningUnix(t);
            if (openAt <= 0) return;

            var next = CloneConfig(t);
            next.Id = store.AllocateId();
            next.CreatedUnix = Now;
            next.OpenAtUnix = openAt;
            store.Mutate(d => d.Active.Add(next));

            Announce(Lang.Get("canevents:tournament-repeat-scheduled", next.Name,
                Math.Max(1, (openAt - Now + 59) / 60)));
            PushUpdate(next.Id);
        }

        /// <summary>
        /// When the next run opens. Times of day win over the plain interval. Both are measured from
        /// now (the end of the run that just finished), so a long bracket never overlaps its successor.
        /// </summary>
        private long NextOpeningUnix(Tournament t)
        {
            var times = t.RepeatAtMinutesOfDay;
            if (times != null && times.Count > 0)
            {
                var now = DateTime.Now;
                int nowMinute = now.Hour * 60 + now.Minute;
                int best = int.MaxValue;
                foreach (int raw in times)
                {
                    int m = ((raw % 1440) + 1440) % 1440;
                    // Strictly later than now: equal would re-open within the same minute the last
                    // run ended, turning a daily tournament into a loop.
                    int delta = m > nowMinute ? m - nowMinute : m + 1440 - nowMinute;
                    if (delta < best) best = delta;
                }
                if (best == int.MaxValue) return 0;
                return Now + best * 60L;
            }

            return t.RepeatEveryMinutes > 0 ? Now + t.RepeatEveryMinutes * 60L : 0;
        }

        /// <summary>Everything that describes HOW a tournament is run, and nothing that happened in one.</summary>
        private static Tournament CloneConfig(Tournament t) => new Tournament
        {
            Name = t.Name,
            EventCodes = new List<string>(t.EventCodes),
            PlaceWhitelist = t.PlaceWhitelist == null ? null : new List<long>(t.PlaceWhitelist),
            TeamSize = t.TeamSize,
            MaxEntrants = t.MaxEntrants,
            EntryFee = t.EntryFee?.Select(i => new RewardItem { Code = i.Code, Quantity = i.Quantity }).ToList(),
            RakePercent = t.RakePercent,
            PotSplitPercents = new List<int>(t.PotSplitPercents),
            PlacementPrizes = t.PlacementPrizes?
                .Select(tier => new RewardTier
                {
                    Items = tier?.Items?.Select(i => new RewardItem { Code = i.Code, Quantity = i.Quantity }).ToList()
                }).ToList(),
            ThirdPlaceMatch = t.ThirdPlaceMatch,
            PartiesPerMatch = t.PartiesPerMatch,
            AdvancePerMatch = t.AdvancePerMatch,
            ReadyCheckSeconds = t.ReadyCheckSeconds,
            MaxConcurrentMatches = t.MaxConcurrentMatches,
            ManualMatchStart = t.ManualMatchStart,
            AutoStartEntrants = t.AutoStartEntrants,
            AutoStartDelaySeconds = t.AutoStartDelaySeconds,
            LockLoadout = t.LockLoadout,
            AllowedLoadouts = new List<string>(t.AllowedLoadouts),
            BringOwnGear = t.BringOwnGear,
            SeedMode = t.SeedMode,
            RatedMode = t.RatedMode,
            PauseScheduler = t.PauseScheduler,
            LockEntrantsWhileRunning = t.LockEntrantsWhileRunning,
            SignupWindowMinutes = t.SignupWindowMinutes,
            RepeatEveryMinutes = t.RepeatEveryMinutes,
            RepeatAtMinutesOfDay = new List<int>(t.RepeatAtMinutesOfDay ?? new List<int>()),
            ShowBeforeSignup = t.ShowBeforeSignup
        };

        private static bool AllMatchesClosed(Tournament t)
        {
            foreach (var m in t.Matches) if (!m.IsClosed) return false;
            return t.Matches.Count > 0;
        }

        // admin lifecycle

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
            return (true, "created tournament #" + t.Id + " '" + t.Name + "'");
        }

        /// <summary>
        /// Applies one configuration edit, only before the bracket exists: changing the fee, split or
        /// arena pool mid-run would move money and rules under people who already paid.
        /// </summary>
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

        /// <summary>The id to assume when a player omits it, or 0 when that would be a guess.</summary>
        public long SoleActiveId()
        {
            var active = store.ActiveSnapshot()
                .Where(t => t.State != TournamentState.Finished && t.State != TournamentState.Cancelled)
                .ToList();
            return active.Count == 1 ? active[0].Id : 0;
        }

        public (bool ok, string message) OpenSignup(long id) => Transition(id, t =>
        {
            if (t.State != TournamentState.Draft) return (false, "signup can only open from draft");
            if (t.EventCodes.Count == 0) return (false, "no event configured");
            t.State = TournamentState.Signup;
            t.OpenAtUnix = 0;   // it is open; the scheduled moment has been spent
            // Stored as a duration so a repeat can re-derive it; here it becomes a deadline.
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

        /// <summary>Seeds the field, builds the bracket and starts calling pairs.</summary>
        public (bool ok, string message) Begin(long id) => Transition(id, t =>
        {
            if (t.State == TournamentState.Signup && t.Entrants.Count >= 2) t.State = TournamentState.Seeded;
            if (t.State != TournamentState.Seeded) return (false, "tournament is not ready to begin");
            if (t.Entrants.Count < 2) return (false, "need at least two entrants");

            // A disabled event can never be called, so refuse here where the admin is looking rather
            // than stall silently later.
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
                if (m.RunningEventId != 0)
                {
                    var re = manager.FindRunningEvent(m.RunningEventId);
                    re?.RequestFinish("tournament cancelled");
                    // Forget the id: RunningEvent ids restart from 1, so a stale one could capture
                    // another tournament's match events.
                    m.RunningEventId = 0;
                }
                m.ReadyDeadlineUnix = 0;
                MarkCalled(t, m, false);
            }

            if (refund) RefundAllContributions(t);
            else
            {
                // Cancelling WITHOUT a refund sinks the stakes on purpose; clear them so a later
                // Delete (which refunds what remains) cannot undo the penalty.
                t.Contributions.Clear();
                t.Pot.Clear();
            }

            t.State = TournamentState.Cancelled;
            t.FinishedUnix = Now;
            ResumeSchedulerIfIdle();
            Announce(Lang.Get("canevents:tournament-cancelled", t.Name));
            return (true, "cancelled");
        });

        /// <summary>
        /// Removes a tournament outright, leaving no history row - for the ones created by mistake;
        /// Cancel is the normal way out. Stakes are always refunded first.
        /// </summary>
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

        /// <summary>Admin override: declares a winner, finishing the live match first if there is one.</summary>
        public (bool ok, string message) SetMatchResult(long id, int matchId, string sideOrEntrantId) => Transition(id, t =>
        {
            var m = t.FindMatch(matchId);
            if (m == null) return (false, "no match #" + matchId);
            if (m.IsClosed) return (false, "match #" + matchId + " is already decided");

            string winner = ResolveSide(t, m, sideOrEntrantId);
            if (winner == null) return (false, "cannot tell which side you mean: " + sideOrEntrantId);

            if (m.RunningEventId != 0)
            {
                var re = manager.FindRunningEvent(m.RunningEventId);
                re?.RequestFinish("tournament admin override");
            }

            CloseMatch(t, m, winner, "admin");
            return (true, "match #" + matchId + " awarded to " + t.NameOfEntrant(winner));
        });

        /// <summary>
        /// Fills the field with bot entrants - a testing tool. Bots pay no fee, take no prize and never
        /// touch Elo, so a bot bracket is a rehearsal, not a result.
        /// </summary>
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

                // Named from the uids, like everywhere else: naming by position instead made the
                // bracket and the arena disagree about who was who.
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
                if (m.IsClosed || !m.Contains(e.Id)) continue;

                if (m.RunningEventId != 0)
                {
                    var re = manager.FindRunningEvent(m.RunningEventId);
                    re?.RequestFinish("tournament disqualification");
                }

                // What is left decides it: one side walks through, none voids it, two or more play on.
                DropSide(m, e.Id);
                var left = m.ActiveSides;
                if (left.Count == 0) CloseMatch(t, m, null, "void");
                else if (left.Count == 1) CloseMatch(t, m, left[0], "technical");
            }

            Announce(Lang.Get("canevents:tournament-disqualified", e.DisplayName, t.Name));
            return (true, e.DisplayName + " disqualified");
        });

        // player operations

        public (bool ok, string message) Signup(IServerPlayer player, long id, string teamName = null)
            => Transition(id, t =>
        {
            if (t.State != TournamentState.Signup) return (false, Lang.Get("canevents:tournament-signup-closed"));
            if (t.Entrants.Count >= t.MaxEntrants) return (false, Lang.Get("canevents:tournament-signup-full"));
            if (t.EntrantOfPlayer(player.PlayerUID) != null)
                return (false, Lang.Get("canevents:tournament-signup-already"));

            // One tournament at a time: two brackets calling the same player is unresolvable.
            foreach (var other in store.ActiveSnapshot())
                if (other.Id != t.Id && other.State != TournamentState.Cancelled
                    && other.State != TournamentState.Finished
                    && other.EntrantOfPlayer(player.PlayerUID) != null)
                    return (false, Lang.Get("canevents:tournament-signup-other", other.Name));

            var uids = ResolveEntrantRoster(t, player, out string rosterError);
            if (uids == null) return (false, rosterError);

            var fee = ChargeEntryFee(t, uids, out string feeError);
            if (!fee) return (false, feeError);

            var entrant = new Entrant
            {
                Id = Guid.NewGuid().ToString(),
                Uids = uids,
                TeamName = CleanShortName(teamName),
                MembersText = string.Join(", ", uids.Select(NameOf))
            };
            entrant.DisplayName = LabelFor(entrant);
            if (!IsTeamNameFree(t, entrant.TeamName, entrant.Id))
            {
                entrant.TeamName = null;
                entrant.DisplayName = LabelFor(entrant);
            }
            t.Entrants.Add(entrant);
            return (true, Lang.Get("canevents:tournament-signup-ok", t.Name, t.Entrants.Count, t.MaxEntrants));
        });

        public (bool ok, string message) Withdraw(IServerPlayer player, long id) => Transition(id, t =>
        {
            var e = t.EntrantOfPlayer(player.PlayerUID);
            if (e == null) return (false, Lang.Get("canevents:tournament-not-entered"));
            if (t.State != TournamentState.Signup && t.State != TournamentState.Seeded)
                return (false, Lang.Get("canevents:tournament-withdraw-late"));
            // Pulling the whole team out is the leader's call: otherwise one teammate ends the run
            // for everyone else.
            if (!IsEntrantLeader(t, e.Id, player.PlayerUID, out string leaderName))
                return (false, Lang.Get("canevents:tournament-leader-only", leaderName));

            RefundContributions(t, e.Uids);
            t.Entrants.Remove(e);
            return (true, Lang.Get("canevents:tournament-withdraw-ok", t.Name));
        });

        /// <summary>
        /// Lets a side name itself, up until the bracket is built; otherwise the default "Leader +4"
        /// label is used.
        /// </summary>
        public (bool ok, string message) SetTeamName(IServerPlayer player, long id, string name)
            => Transition(id, t =>
        {
            var e = t.EntrantOfPlayer(player.PlayerUID);
            if (e == null) return (false, Lang.Get("canevents:tournament-not-entered"));
            if (t.State != TournamentState.Signup && t.State != TournamentState.Seeded)
                return (false, Lang.Get("canevents:tournament-name-late"));
            // The leader owns the side's name - otherwise the last member to type wins.
            if (!IsEntrantLeader(t, e.Id, player.PlayerUID, out string leaderName))
                return (false, Lang.Get("canevents:tournament-leader-only", leaderName));

            string clean = CleanShortName(name);
            if (!IsTeamNameFree(t, clean, e.Id))
                return (false, Lang.Get("canevents:tournament-name-taken"));

            e.TeamName = clean;
            e.DisplayName = LabelFor(e);
            return (true, Lang.Get("canevents:tournament-name-ok", e.DisplayName));
        });

        /// <summary>
        /// Picks the class this player uses for the whole tournament. Allowed right up to the last
        /// moment, since changing it costs nobody anything.
        /// </summary>
        public (bool ok, string message) SetLoadout(IServerPlayer player, long id, string setName)
            => Transition(id, t =>
        {
            var e = t.EntrantOfPlayer(player.PlayerUID);
            if (e == null) return (false, Lang.Get("canevents:tournament-not-entered"));

            // Locked tournaments fix the pick once the bracket is under way.
            if (t.LockLoadout && t.State == TournamentState.Running
                && e.LoadoutChoices.ContainsKey(player.PlayerUID))
                return (false, Lang.Get("canevents:tournament-class-locked"));

            string clean = CleanShortName(setName);
            if (string.IsNullOrEmpty(clean))
            {
                e.LoadoutChoices.Remove(player.PlayerUID);
                return (true, Lang.Get("canevents:tournament-class-cleared"));
            }

            if (!t.AllowsLoadout(clean))
                return (false, Lang.Get("canevents:tournament-class-banned",
                    string.Join(", ", t.AllowedLoadouts)));

            // Not validated against the event: a rotation may include modes that lack this set, and
            // refusing here would refuse a pick that is valid for the other mode.
            e.LoadoutChoices[player.PlayerUID] = clean;
            return (true, Lang.Get("canevents:tournament-class-ok", clean));
        });

        /// <summary>
        /// Trims a player-supplied short name (team or class pick), caps its length and drops anything
        /// that would break a chat line or a bracket box.
        /// </summary>
        private static string CleanShortName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var cleaned = new string(name.Trim().Where(c => !char.IsControl(c) && c != '<' && c != '>').ToArray());
            if (cleaned.Length > 24) cleaned = cleaned.Substring(0, 24);
            return cleaned.Length == 0 ? null : cleaned;
        }

        private static bool IsTeamNameFree(Tournament t, string name, string ownEntrantId)
        {
            if (string.IsNullOrEmpty(name)) return true;
            foreach (var e in t.Entrants)
                if (e.Id != ownEntrantId
                    && string.Equals(e.TeamName, name, StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        /// <summary>The label a bracket box shows: the chosen name, a solo player's name, or "Leader +N".</summary>
        private string LabelFor(Entrant e)
        {
            if (!string.IsNullOrEmpty(e.TeamName)) return e.TeamName;
            if (e.Uids == null || e.Uids.Count == 0) return "?";
            if (e.Uids.Count == 1) return NameOf(e.Uids[0]);
            return NameOf(e.Uids[0]) + " +" + (e.Uids.Count - 1);
        }

        /// <summary>Reconnect hook: hands over anything owed and re-sends a live ready check.</summary>
        public void OnReconnect(IServerPlayer player)
        {
            DeliverPendingPayouts(player);
            ResendReadyCheck(player);
        }

        // restart recovery

        /// <summary>
        /// No RunningEvent survives a restart, so any match in flight goes back to Pending and is
        /// called again once both sides are online. Bracket, pot and results are intact in the store.
        /// </summary>
        public void RecoverAfterRestart()
        {
            store.Mutate(d =>
            {
                foreach (var t in d.Active)
                {
                    if (t.State != TournamentState.Running) continue;
                    int reset = 0;
                    foreach (var m in t.Matches)
                    {
                        if (m.State != BracketMatchState.AwaitingReady
                            && m.State != BracketMatchState.Starting
                            && m.State != BracketMatchState.Live) continue;

                        m.State = BracketMatchState.Pending;
                        m.RunningEventId = 0;
                        m.ReadyDeadlineUnix = 0;
                        m.ReadyUids.Clear();
                        reset++;
                    }
                    if (reset > 0)
                    {
                        log.Notification("{0}: {1} interrupted match(es) reset to pending after restart",
                            t.Id, reset);
                        Announce(Lang.Get("canevents:tournament-resumed", t.Name));
                    }
                }
            });

            lock (calledUids) calledUids.Clear();

            // The scheduler's off switch is persisted but the "we turned it off" flag is not; without
            // rebuilding it the scheduler would stay off for good once the bracket ended.
            foreach (var t in store.ActiveSnapshot())
            {
                if (t.State == TournamentState.Running && t.PauseScheduler
                    && scheduler != null && !scheduler.Enabled)
                {
                    schedulerPausedByUs = true;
                    break;
                }
            }
        }

        // helpers

        /// <summary>Runs an operation against one tournament under the store lock, saving on success.</summary>
        private (bool ok, string message) Transition(long id, System.Func<Tournament, (bool, string)> op)
        {
            var result = store.MutateChecked(d =>
            {
                var target = d.Active.FirstOrDefault(x => x.Id == id);
                if (target == null) return (false, "no tournament #" + id);
                return op(target);
            });
            if (result.ok) PushUpdate(id);
            return result;
        }

        /// <summary>Re-sends the list to everyone and the bracket to whoever is looking at it.</summary>
        internal void PushUpdate(long id) => manager.NetworkHandler?.BroadcastTournamentUpdate(id);

        /// <summary>
        /// How many sides one bracket match seats: configured explicitly, or taken from what the
        /// tournament's events seat. Clamped to what every chosen event can host, so a rotation never
        /// calls a match a mode cannot seat.
        /// </summary>
        public int ResolveArity(Tournament t)
        {
            if (t.PartiesPerMatch >= 2) return CapByEvents(t, t.PartiesPerMatch);

            int seats = 0;
            foreach (var code in t.EventCodes)
            {
                var ev = manager.FindEventByCode(code);
                int max = ev?.AssemblingEB?.MaxPartiesCount ?? 2;
                if (max > seats) seats = max;
            }
            return CapByEvents(t, Math.Max(2, seats));
        }

        private int CapByEvents(Tournament t, int wanted)
        {
            foreach (var code in t.EventCodes)
            {
                var ev = manager.FindEventByCode(code);
                int max = ev?.AssemblingEB?.MaxPartiesCount ?? 2;
                if (max >= 2 && max < wanted) wanted = max;
            }
            return Math.Max(2, wanted);
        }

        /// <summary>Seeds the field: random, or strongest-first by Elo across the tournament's events.</summary>
        private List<Entrant> SeedEntrants(Tournament t)
        {
            var list = new List<Entrant>(t.Entrants);
            if (t.SeedMode == 1 && manager.Leaderboard != null)
                list = list.OrderByDescending(RatingOf).ToList();
            else
                BracketBuilder.Shuffle(list, rng);

            for (int i = 0; i < list.Count; i++) list[i].Seed = i + 1;
            return list;

            int RatingOf(Entrant e)
            {
                int sum = 0, n = 0;
                foreach (var uid in e.Uids)
                    foreach (var code in t.EventCodes)
                    {
                        sum += manager.Leaderboard.GetRating(uid, code);
                        n++;
                    }
                return n == 0 ? Scoring.EloCalculator.DefaultRating : sum / n;
            }
        }

        /// <summary>
        /// The uids this signup enrols: the player alone, or their whole party. Frozen from here on,
        /// so the entrant survives party churn.
        /// </summary>
        private List<string> ResolveEntrantRoster(Tournament t, IServerPlayer player, out string error)
        {
            error = null;
            if (t.TeamSize <= 1) return new List<string> { player.PlayerUID };

            var party = manager.PartyRosterOf(player, out bool isLeader);
            if (party == null || party.Count != t.TeamSize)
            {
                error = Lang.Get("canevents:tournament-signup-party", t.TeamSize);
                return null;
            }
            if (!isLeader)
            {
                error = Lang.Get("canevents:queue-party-leader-only");
                return null;
            }
            foreach (var uid in party)
            {
                if (!(sapi.World.PlayerByUid(uid) is IServerPlayer))
                {
                    error = Lang.Get("canevents:tournament-signup-offline", NameOf(uid));
                    return null;
                }
                foreach (var other in store.ActiveSnapshot())
                    if (other.State != TournamentState.Cancelled && other.State != TournamentState.Finished
                        && other.EntrantOfPlayer(uid) != null)
                    {
                        error = Lang.Get("canevents:tournament-signup-other", other.Name);
                        return null;
                    }
            }
            return new List<string>(party);
        }

        private Entrant FindEntrantLoose(Tournament t, string idOrName)
        {
            if (string.IsNullOrEmpty(idOrName)) return null;
            var byId = t.FindEntrant(idOrName);
            if (byId != null) return byId;
            foreach (var e in t.Entrants)
            {
                if (string.Equals(e.DisplayName, idOrName, StringComparison.OrdinalIgnoreCase)) return e;
                foreach (var uid in e.Uids)
                    if (string.Equals(NameOf(uid), idOrName, StringComparison.OrdinalIgnoreCase)) return e;
            }
            return null;
        }

        /// <summary>
        /// Accepts a slot letter, a slot number, an entrant id or a player name. Only sides actually
        /// in this match resolve.
        /// </summary>
        private string ResolveSide(Tournament t, BracketMatch m, string token)
        {
            if (string.IsNullOrEmpty(token)) return null;

            var filled = m.FilledSlots;
            if (token.Length == 1 && char.IsLetter(token[0]))
            {
                int index = char.ToLowerInvariant(token[0]) - 'a';
                if (index >= 0 && index < filled.Count) return filled[index];
            }
            if (int.TryParse(token, out int slot) && slot >= 1 && slot <= filled.Count)
                return filled[slot - 1];

            var e = FindEntrantLoose(t, token);
            return e != null && m.Contains(e.Id) ? e.Id : null;
        }

        internal string NameOf(string uid)
            => Bots.BotIdentity.IsBot(uid)
                ? Bots.BotIdentity.NameOfUid(uid)
                : sapi.World.PlayerByUid(uid)?.PlayerName ?? uid;

        private void Announce(string message) => EventBroadcaster.BroadcastToServer(sapi, message);

        private void Tell(Tournament t, BracketMatch m, string message)
        {
            foreach (var id in m.ActiveSides)
            {
                var e = t.FindEntrant(id);
                if (e?.Uids == null) continue;
                foreach (var uid in e.Uids)
                    (sapi.World.PlayerByUid(uid) as IServerPlayer)?
                        .SendMessage(0, message, EnumChatType.Notification);
            }
        }

        // read-only reports (chat; the GUI feeds off the same data)

        /// <summary>One line per active tournament.</summary>
        public List<string> Describe()
        {
            var lines = new List<string>();
            foreach (var t in store.ActiveSnapshot())
                lines.Add(string.Format("#{0} '{1}' [{2}] {3} entrant(s), events: {4}, pot: {5}",
                    t.Id, t.Name, t.State, t.Entrants.Count, string.Join("/", t.EventCodes),
                    DescribePot(t.Pot)));
            if (lines.Count == 0) lines.Add("no active tournaments");
            return lines;
        }

        /// <summary>
        /// The bracket as plain text, round by round - for logs and support.
        /// </summary>
        public List<string> DescribeBracket(long id)
        {
            var t = store.FindActive(id) ?? store.Read(d => d.History.FirstOrDefault(x => x.Id == id));
            if (t == null) return new List<string> { "no tournament #" + id };

            var lines = new List<string>
            {
                string.Format("#{0} '{1}' [{2}] - {3} entrant(s), pot: {4}",
                    t.Id, t.Name, t.State, t.Entrants.Count, DescribePot(t.Pot))
            };

            if (t.Matches.Count == 0)
            {
                foreach (var e in t.Entrants) lines.Add("  - " + e.DisplayName);
                return lines;
            }

            int rounds = BracketBuilder.TotalRounds(t.Matches);
            foreach (var group in t.Matches.GroupBy(m => m.Round).OrderBy(g => g.Key))
            {
                lines.Add("-- " + BracketBuilder.RoundLabel(group.First(), rounds) + " --");
                foreach (var m in group.OrderBy(m => m.SlotInRound))
                {
                    // Every slot, joined: "A vs B" for a duel, all of them for a heat.
                    string sides = m.Slots.Count == 0
                        ? "-"
                        : string.Join(" vs ", m.Slots.Select(id =>
                            string.IsNullOrEmpty(id) ? "TBD" : t.NameOfEntrant(id)));
                    string mark = m.State == BracketMatchState.Live ? " <LIVE>"
                        : m.State == BracketMatchState.AwaitingReady
                            ? " <ready " + Math.Max(0, m.ReadyDeadlineUnix - Now) + "s>"
                            : m.IsClosed && m.WinnerEntrantId != null
                                ? " -> " + t.NameOfEntrant(m.WinnerEntrantId) + " (" + m.ResultKind + ")"
                                : m.State == BracketMatchState.Void ? " -> void" : "";
                    string label = m.IsThirdPlace ? "[3rd] " : "";
                    lines.Add(string.Format("  #{0} {1}{2}{3}", m.Id, label, sides, mark));
                }
            }

            if (t.FinalPlacement.Count > 0)
                lines.Add("Final: " + string.Join(", ", t.FinalPlacement
                    .Select((eid, i) => (i + 1) + ". " + t.NameOfEntrant(eid))));

            return lines;
        }

        private void ResumeSchedulerIfIdle()
        {
            if (!schedulerPausedByUs || scheduler == null) return;
            foreach (var t in store.ActiveSnapshot())
                if (t.State == TournamentState.Running && t.PauseScheduler) return;
            scheduler.SetEnabled(true);
            schedulerPausedByUs = false;
        }
    }
}
