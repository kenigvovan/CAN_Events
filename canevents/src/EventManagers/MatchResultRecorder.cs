using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.Scoring;
using Vintagestory.API.Server;

namespace canevents.src.EventManagers
{
    /// <summary>
    /// Everything that happens to a finished match's outcome: rewards, standings (leaderboard + Elo),
    /// the result screen and the history entry. The second half of the finish sequence: the manager
    /// freezes the outcome into a <see cref="MatchResult"/>, tears the match down, then hands the
    /// frozen copy here.
    /// </summary>
    public class MatchResultRecorder
    {
        private readonly CANEventManager manager;
        private readonly ICoreServerAPI sapi;
        private readonly Config config;

        public MatchResultRecorder(CANEventManager manager, ICoreServerAPI sapi, Config config)
        {
            this.manager = manager;
            this.sapi = sapi;
            this.config = config;
        }

        public void Record(RunningEvent runningEvent, MatchResult result)
        {
            manager.RewardDistributor?.DistributeRewards(runningEvent, result);

            RecordStandings(runningEvent, result);
            PublishResult(runningEvent, result);
        }

        // Ranking counts on the RATED track only; unrated events are casual. Rewards are independent
        // and are distributed by the caller on both tracks.
        private void RecordStandings(RunningEvent runningEvent, MatchResult result)
        {
            var leaderboard = manager.Leaderboard;
            if (!runningEvent.Event.Rated || leaderboard == null) return;
            // An exhibition bracket runs real rated events but must not move anyone's rating.
            if (runningEvent.SuppressRating) return;

            // A bot-padded lobby produces no standings at all, humans included: rating play against
            // bots is the cheapest way to farm Elo, and dropping only the bot rows would still hand the
            // humans a free win. Rated events shouldn't be configured with bots; this is the backstop.
            if (runningEvent.ParticipantsBefore.Any(Bots.BotIdentity.IsBot))
            {
                sapi.World.Logger.Notification(
                    "Skipping leaderboard/Elo for {0}: the lobby contained bots.", runningEvent.Event.Code);
                return;
            }

            // Same anti-throw contribution gate the reward path uses: a match shorter than the
            // threshold is a suspected win-trade, so it must not move Elo or the win/loss ledger
            // either - otherwise two accounts farm ranked standings with instant matches.
            if (RewardResolver.BelowContributionGate(runningEvent.Event, config,
                    runningEvent.ElapsedSeconds, out int minSeconds))
            {
                sapi.World.Logger.Notification(
                    "Skipping leaderboard/Elo for {0}: match too short ({1:0}s < {2}s) - anti-throw.",
                    runningEvent.Event.Code, runningEvent.ElapsedSeconds, minSeconds);
                return;
            }

            string eventCode = runningEvent.Event.Code;
            if (result.Winners.Count == 0)
            {
                // No winner = a genuine draw (time-up with the top sides tied); recording a loss would
                // punish both sides of a tie.
                foreach (var uid in runningEvent.ParticipantsBefore)
                    leaderboard.RecordDraw(uid, eventCode);
            }
            else
            {
                foreach (var uid in result.Winners)
                    leaderboard.RecordWin(uid, eventCode);
                foreach (var uid in runningEvent.ParticipantsBefore)
                    if (!result.Won(uid)) leaderboard.RecordLoss(uid, eventCode);
            }

            RecordElo(runningEvent, result, eventCode);
        }

        // Elo for any mode that produces a ranking. Sides are the starting rosters
        // (ParticipantsByGroupsBefore, so disconnected players still count); each side's placement is
        // the index of the result.Ranking bracket holding any of its members, unranked → last. Skipped
        // when the scorer can't rank or there are fewer than 2 sides.
        private void RecordElo(RunningEvent runningEvent, MatchResult result, string eventCode)
        {
            var sides = runningEvent.ParticipantsByGroupsBefore;
            var ranking = result.Ranking;
            if (ranking == null || sides == null || sides.Count < 2) return;

            var sideRosters = new List<IReadOnlyList<string>>(sides.Count);
            var ranks = new int[sides.Count];
            for (int i = 0; i < sides.Count; i++)
            {
                sideRosters.Add(sides[i].ToList());
                int idx = ranking.FindIndex(bracket => sides[i].Any(bracket.Contains));
                ranks[i] = idx >= 0 ? idx : ranking.Count;
            }
            manager.Leaderboard.RecordEloResult(eventCode, sideRosters, ranks);
        }

        // One scoreboard, read once: the network message and the history entry are two shapes of the
        // same rows, and building them separately is how they drift.
        private void PublishResult(RunningEvent runningEvent, MatchResult result)
        {
            // Bot bodies are already gone by now, so their names come from the bot manager rather than
            // from the world.
            string NameOfParticipant(string uid)
                => Bots.BotIdentity.IsBot(uid)
                    // The uid-derived name is the fallback for a bot that never got a body at all.
                    ? runningEvent.Bots.NameOf(uid) ?? Bots.BotIdentity.NameOfUid(uid)
                    : NameOf(uid);

            var rows = runningEvent.ParticipantsBefore.Select(uid => new
            {
                Name   = NameOfParticipant(uid),
                Kills  = result.KillsOf(uid),
                Deaths = result.DeathsOf(uid),
                Won    = result.Won(uid)
            }).ToList();

            var winnerNames = result.Winners.Select(NameOfParticipant).ToArray();

            var networkHandler = manager.NetworkHandler;
            if (networkHandler != null)
            {
                var finalScores = rows.Select(r => new Network.ScoreEntryMsg
                {
                    PlayerName = r.Name,
                    Kills      = r.Kills,
                    Deaths     = r.Deaths,
                    Alive      = r.Won
                }).ToArray();
                networkHandler.SendEventResult(runningEvent, winnerNames, finalScores);
            }

            manager.MatchHistory?.Add(new MatchHistoryEntry
            {
                EventCode       = runningEvent.Event.Code,
                TimestampUnix   = UnixTime.Now,
                WinnerNames     = winnerNames,
                DurationSeconds = (int)runningEvent.ElapsedSeconds,
                Scores          = rows.Select(r => new MatchPlayerScore
                {
                    PlayerName = r.Name,
                    Kills      = r.Kills,
                    Deaths     = r.Deaths,
                    Won        = r.Won
                }).ToList()
            });
        }

        // Shared with the manager, which names the winners in its finish broadcast.
        private string NameOf(string uid) => manager.NameOf(uid);
    }
}
