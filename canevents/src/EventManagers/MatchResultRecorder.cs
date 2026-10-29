using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.Scoring;
using Vintagestory.API.Server;

namespace canevents.src.EventManagers
{
    // Rewards, standings, Elo, result screen and history. Gets the result captured before teardown.
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
            // An open match only ends when an admin closes it: no result, no head-to-head draws, no history.
            if (runningEvent.IsOpenMatch) return;

            manager.RewardDistributor?.DistributeRewards(runningEvent, result);

            RecordStandings(runningEvent, result);
            RecordHeadToHead(runningEvent, result);
            PublishResult(runningEvent, result);
        }

        // Two-sided matches only, unrated ones (duels) included, so it's outside RecordStandings.
        private void RecordHeadToHead(RunningEvent runningEvent, MatchResult result)
        {
            var headToHead = manager.HeadToHead;
            var sides = runningEvent.ParticipantsByGroupsBefore;
            if (headToHead == null || sides == null || sides.Count != 2) return;

            foreach (var a in sides[0])
            {
                if (Bots.BotIdentity.IsBot(a)) continue;
                foreach (var b in sides[1])
                {
                    if (Bots.BotIdentity.IsBot(b)) continue;

                    bool aWon = result.Won(a);
                    bool bWon = result.Won(b);
                    if (aWon == bWon) headToHead.RecordDraw(a, b);
                    else headToHead.RecordWin(aWon ? a : b, aWon ? b : a);
                }
            }
        }

        // Rated events only.
        private void RecordStandings(RunningEvent runningEvent, MatchResult result)
        {
            var leaderboard = manager.Leaderboard;
            if (!runningEvent.Event.Rated || leaderboard == null) return;
            if (runningEvent.SuppressRating) return;

            // Any bot in the lobby skips standings for everyone, otherwise Elo can be farmed off bots.
            if (runningEvent.ParticipantsBefore.Any(Bots.BotIdentity.IsBot))
            {
                sapi.World.Logger.Notification(
                    "Skipping leaderboard/Elo for {0}: the lobby contained bots.", runningEvent.Event.Code);
                return;
            }

            // Same anti win-trade gate as rewards.
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
                // No winner = draw.
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

        // Side placement = index of the first ranking bracket with any of its members; unranked = last.
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

        // Same rows for the network message and the history entry.
        private void PublishResult(RunningEvent runningEvent, MatchResult result)
        {
            // Bot bodies are despawned by now.
            string NameOfParticipant(string uid)
                => Bots.BotIdentity.IsBot(uid)
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

        private string NameOf(string uid) => manager.NameOf(uid);
    }
}
