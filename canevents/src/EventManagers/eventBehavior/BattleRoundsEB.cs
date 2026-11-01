using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers.eventBehavior.abstracts;
using canevents.src.EventManagers.eventBehavior.Shared;
using canevents.src.EventManagers.events;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace canevents.src.EventManagers.eventBehavior
{
    // Rounds without respawns: the last team with someone alive takes the round, first to winsToWin
    // takes the match. The dead watch as spectators (keep spectateOnElimination on). Between rounds
    // everyone is revived at their team's spawn, may change class, and the next round starts with a
    // fresh kit for all.
    public class BattleRoundsEB : KillScoringEB, ISpawnOrderMatters, IDecidesOnDropout, IRoundClock, IStartCryProvider
    {
        public bool RoundLive => roundLive;
        public float RoundSecondsLeft => Math.Max(0f, roundSeconds - (runningEvent.ElapsedSeconds - roundStartedAt));
        public float RoundLength => roundSeconds;
        public string StartCryLangKey => "canevents:battle-start-cry";

        private int winsToWin = 5;
        private float roundSeconds = 180f;
        private float intermissionSeconds = 8f;

        private int[] teamScores = new int[0];
        private int round;
        private bool roundLive;
        private float roundStartedAt;
        private float intermissionEndsAt = -1f;

        // Intermission: nobody takes damage or walks off while they stand at their spawns.
        private readonly DamageHookSet truce = new DamageHookSet();
        private MovementHold hold;

        public BattleRoundsEB(string name, JsonObject attributes, RunningEvent runningEvent)
            : base(name, attributes, runningEvent) { }

        public override string Name => BehaviorNames.BattleRounds;

        public override bool WantsTick => true;

        protected override void ReadModeConfig()
        {
            winsToWin = Math.Max(1, Config.Int("winsToWin", 5));
            roundSeconds = Math.Max(10f, Config.Float("roundTimeLimitSeconds", 180f));
            intermissionSeconds = Math.Max(3f, Config.Float("intermissionSeconds", 8f));
        }

        public override void OnBeforeStart()
        {
            runningEvent.PerTeamSpawns = true;
            InitScores();
            teamScores = new int[runningEvent.ParticipantsByGroupsBefore.Count];
            // Backstop only; the rounds end the match. Each team can win at most winsToWin-1 before
            // someone gets there, plus draws.
            int maxRounds = teamScores.Length * winsToWin + 5;
            runningEvent.SetDuration(maxRounds * (roundSeconds + intermissionSeconds));
        }

        // Round 1 opens with the same held countdown as every later one: everyone is on their spawn by now.
        public override void OnStart()
        {
            GiveLoadoutAndHealAll();
            BeginIntermission(firstRound: true);
        }

        private void BeginRound()
        {
            round++;
            roundLive = true;
            roundStartedAt = runningEvent.ElapsedSeconds;
            Announce("canevents:battle-round-start", round, winsToWin);
            PushScoreboard();
        }

        // Dead players leave play for the rest of the round.
        protected override void AfterScoredDeath(string victimUid, Entity victimBody, KillInfo kill)
            => runningEvent.RemoveFromPlay(victimUid);

        public override void OnTick(float dt)
        {
            if (MatchResolved) return;

            if (!roundLive)
            {
                if (intermissionEndsAt >= 0f && runningEvent.ElapsedSeconds >= intermissionEndsAt) StartNextRound();
                return;
            }

            // Covers deaths and disconnects alike.
            if (Forfeit()) return;

            var alive = TeamsAlive();
            if (alive.Count <= 1)
            {
                EndRound(alive.Count == 1 ? alive[0] : -1);
                return;
            }

            if (runningEvent.ElapsedSeconds - roundStartedAt >= roundSeconds)
            {
                // Time up: more players standing takes it, a tie is a draw.
                var counts = alive.Select(t => (team: t, n: AliveIn(t))).OrderByDescending(x => x.n).ToList();
                EndRound(counts.Count > 1 && counts[0].n == counts[1].n ? -1 : counts[0].team);
            }
        }

        private List<int> TeamsAlive()
            => Enumerable.Range(0, teamScores.Length).Where(t => AliveIn(t) > 0).ToList();

        private int AliveIn(int team)
            => runningEvent.ParticipantsByGroupsBefore[team].Count(runningEvent.IsActive);

        // Still able to come back next round: not sent home, not quit. A bot (debug fill) always is.
        private bool CanPlayOn(string uid)
            => runningEvent.HoldsPlayer(uid) && !runningEvent.LeftInPlay(uid)
               && (Bots.BotIdentity.IsBot(uid) || runningEvent.sapi.World.PlayerByUid(uid) is IServerPlayer);

        // A team with nobody left to field loses the match.
        private bool Forfeit()
        {
            var fielding = Enumerable.Range(0, teamScores.Length)
                .Where(t => runningEvent.ParticipantsByGroupsBefore[t].Any(CanPlayOn)).ToList();
            if (fielding.Count > 1) return false;

            DeclareWinner(fielding.Count == 1 ? fielding[0] : -1);
            return true;
        }

        private void EndRound(int winner)
        {
            roundLive = false;
            if (winner >= 0)
            {
                teamScores[winner]++;
                Announce("canevents:battle-round-won", TeamPalette.NameOf(winner), ScoreLine(), winsToWin);
                MatchShopEB.Of(runningEvent)?.OnRoundWon(WinnersOf(winner));
            }
            else Announce("canevents:battle-round-draw", ScoreLine(), winsToWin);
            PushScoreboard();

            if (winner >= 0 && teamScores[winner] >= winsToWin)
            {
                DeclareWinner(winner);
                return;
            }
            BeginIntermission();
        }

        // firstRound: everyone was just teleported in with a fresh kit (bots get their bodies from ArenaBots),
        // so only the held countdown runs, no revive, reset or class offer.
        private void BeginIntermission(bool firstRound = false)
        {
            intermissionEndsAt = runningEvent.ElapsedSeconds + intermissionSeconds;
            runningEvent.BeginHold(intermissionSeconds, "canevents:battle-next-round");

            // Nobody strikes and nobody walks off their spawn until the round starts.
            runningEvent.CombatSuspended = true;
            hold ??= new MovementHold(runningEvent, "canevents:battle-hold-error");
            hold.Begin();

            var tp = runningEvent.GetBehavior<TPThereAndBack>();
            var bots = runningEvent.GetBehavior<ArenaBotsEB>();
            // Kit the fallen dropped last round: the next round starts on a clean floor.
            if (!firstRound) IssuedGearSweep.Sweep(runningEvent.sapi, runningEvent.Place, log);
            foreach (var uid in firstRound ? new List<string>() : runningEvent.ParticipantsBefore.ToList())
            {
                if (!CanPlayOn(uid)) continue;
                if (Bots.BotIdentity.IsBot(uid))
                {
                    runningEvent.ReturnToPlay(uid);
                    bots?.ResetForRound(uid);
                    continue;
                }
                var player = (IServerPlayer)runningEvent.sapi.World.PlayerByUid(uid);

                runningEvent.Spectator?.RemoveSpectator(player);
                if (player.Entity != null && !player.Entity.Alive) player.Entity.Revive();
                Compat.DownedCompat.Clear(player);
                runningEvent.ReturnToPlay(uid);

                // Emptied now; the full kit comes at the round start.
                if (!runningEvent.KeepOwnGear) ClearForNextRound(player);
                EventBroadcaster.Heal(player);
                EventBroadcaster.Feed(player);
                tp?.SendToSpawn(player, "next round ");

                runningEvent.NetworkHandler?.RequestLoadoutPickFor(runningEvent, player, intermissionSeconds,
                    "canevents:gui-class-change-closes-in", null);
            }

            // Only those playing on; someone already sent home is back in the world and must stay hittable.
            truce.HookAll(runningEvent, runningEvent.ParticipantsBefore.Where(CanPlayOn),
                (uid, damage, source) => roundLive || !runningEvent.IsActive(uid) ? damage : 0f, includeBots: true);
            runningEvent.NetworkHandler?.SendStateUpdate(runningEvent);
        }

        private void StartNextRound()
        {
            intermissionEndsAt = -1f;
            truce.UnhookAll();
            hold?.End();
            runningEvent.CombatSuspended = false;

            // At the round start, not the intermission's: the truce only stops damage, so a stun or debuff
            // cast during the countdown would otherwise open the round.
            foreach (var uid in runningEvent.ParticipantsBefore)
                if (runningEvent.IsActive(uid)) Compat.RpgClassesCompat.ResetCombat(runningEvent.BodyOf(uid));

            foreach (var player in runningEvent.ActiveOnlineParticipants)
            {
                // Anything bought during the intermission is in the inventory by now.
                if (!runningEvent.KeepOwnGear) ClearForNextRound(player);
                GiveLoadoutAndHeal(player);
            }
            BeginRound();
            // After BeginRound: the state EndHold sends carries the new round's clock.
            runningEvent.EndHold();
        }

        private void DeclareWinner(int team)
        {
            roundLive = false;
            bool valid = team >= 0 && team < teamScores.Length;
            if (valid) Announce("canevents:battle-match-won", TeamPalette.NameOf(team), ScoreLine());
            ResolveMatch(valid ? WinnersOf(team) : null, "battle decided");
        }

        // Whoever left (lobby dropout, quitter, sent home) doesn't share the win.
        private IEnumerable<string> WinnersOf(int team)
            => runningEvent.ParticipantsByGroupsBefore[team]
                .Where(uid => runningEvent.HoldsPlayer(uid) && !runningEvent.LeftInPlay(uid))
                .ToList();

        // Time backstop or admin stop: most rounds won, a tie is a draw.
        public override void OnBeforeFinish()
        {
            if (MatchResolved) return;
            int best = ScoringUtil.LeadingTeam(teamScores);
            ResolveMatch(best >= 0 ? WinnersOf(best) : null, "battle time up", requestFinish: false);
        }

        public override void OnEnd()
        {
            truce.UnhookAll();
            hold?.End();
            // Called off in the waiting room: there was no battle to sum up.
            if (round > 0) Announce("canevents:battle-ended", ScoreLine());
        }

        private string ScoreLine() => ScoringUtil.ScoreLine(teamScores);

        public override ScoreboardData BuildScoreboard()
        {
            var data = base.BuildScoreboard();
            data.TeamScores = teamScores;
            return data;
        }

        // Rounds won, like TeamScoreEB.
        public override List<HashSet<string>> GetFinalRanking()
            => ScoringUtil.RankTeams(runningEvent.ParticipantsByGroupsBefore, runningEvent.GetWinners(), teamScores);
    }
}
