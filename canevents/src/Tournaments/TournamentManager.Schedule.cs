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
        private void OnTick(float dt)
        {
            var active = store.ActiveSnapshot();
            foreach (var t in active)
            {
                log.Safely("tournament " + t.Id + " tick", () =>
                {
                    switch (t.State)
                    {
                        case TournamentState.Draft:
                            if (t.OpenAtUnix > 0 && Now >= t.OpenAtUnix)
                            {
                                var opened = OpenSignup(t.Id);
                                if (!opened.ok)
                                    log.Warning("tournament {0}: scheduled open failed - {1}", t.Id, opened.message);
                            }
                            break;

                        case TournamentState.Signup:
                            if (CheckAutoStart(t) || (t.SignupDeadlineUnix > 0 && Now >= t.SignupDeadlineUnix))
                                AutoBegin(t);
                            break;

                        case TournamentState.Seeded:
                            // A withdrawal between CloseSignup and Begin can strand it here; reopen signup.
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

        // Arms/disarms the auto-start countdown, true when it fires.
        private bool CheckAutoStart(Tournament t)
        {
            if (t.AutoStartEntrants <= 0) return false;

            if (t.Entrants.Count < Math.Max(2, t.AutoStartEntrants))
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

        private const int AutoStartRetrySeconds = 300;

        // On failure: back to Signup, clear the deadline and back off the countdown so it doesn't retry every tick.
        private void AutoBegin(Tournament t)
        {
            var result = CloseSignup(t.Id);
            if (result.ok)
            {
                result = Begin(t.Id);
                if (!result.ok)
                    store.Mutate(_ => { if (t.State == TournamentState.Seeded) t.State = TournamentState.Signup; });
            }
            if (result.ok) return;

            store.Mutate(_ =>
            {
                t.SignupDeadlineUnix = 0;
                if (t.AutoStartAtUnix != 0) t.AutoStartAtUnix = Now + AutoStartRetrySeconds;
            });
            Announce(Lang.Get("canevents:tournament-autostart-failed", t.Name, result.message));
            PushUpdate(t.Id);
        }

        private const int ArchiveAfterSeconds = 15 * 60;

        private void ArchiveFinished(Tournament t)
        {
            // Fallback for old records without a finish time.
            if (t.FinishedUnix == 0)
            {
                store.Mutate(_ => t.FinishedUnix = Now);
                return;
            }
            if (Now - t.FinishedUnix < ArchiveAfterSeconds) return;

            store.Archive(t);
            PushUpdate(t.Id);
        }

        // Called from Finalize inside its store write. Cancelled tournaments don't repeat.
        private Tournament PrepareRepeat(Tournament t, TournamentData d)
        {
            if (!t.Repeats) return null;

            long openAt = NextOpeningUnix(t);
            if (openAt <= 0) return null;

            var next = CloneConfig(t);
            next.Id = d.NextId++;
            next.CreatedUnix = Now;
            next.OpenAtUnix = openAt;
            d.Active.Add(next);
            return next;
        }

        // Measured from the end of the previous run. Times of day override the interval.
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
                    // Strictly later, or it reopens in the same minute.
                    int delta = m > nowMinute ? m - nowMinute : m + 1440 - nowMinute;
                    if (delta < best) best = delta;
                }
                if (best == int.MaxValue) return 0;
                return Now + best * 60L;
            }

            return t.RepeatEveryMinutes > 0 ? Now + t.RepeatEveryMinutes * 60L : 0;
        }

        // Config only, no run state.
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
    }
}
