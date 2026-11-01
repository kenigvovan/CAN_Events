using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers;
using canevents.src.EventManagers.eventBehavior.Shared;
using Vintagestory.API.Server;

namespace canevents.src.Network
{
    // HUD packets. Behaviors go through EventBroadcaster, not here.
    public partial class ServerNetworkHandler
    {
        // Participants plus observers.
        private void SendToAudience<T>(RunningEvent re, T msg)
        {
            foreach (var player in re.HudAudience) serverChannel.SendPacket(msg, player);
        }

        public void SendStateUpdate(RunningEvent re) => SendToAudience(re, BuildStateUpdate(re));

        // For someone who starts watching mid-match; the HUD opens on a state packet.
        public void SendStateUpdateTo(RunningEvent re, IServerPlayer player)
        {
            if (re == null || player == null) return;
            serverChannel.SendPacket(BuildStateUpdate(re), player);
        }

        // Fake Finished state so an observer who left gets their HUD closed.
        public void SendMatchClosedTo(RunningEvent re, IServerPlayer player)
        {
            if (re == null || player == null) return;

            var msg = BuildStateUpdate(re);
            msg.EventState = EventState.Finished;
            serverChannel.SendPacket(msg, player);
        }

        private static EventStateUpdateMsg BuildStateUpdate(RunningEvent re)
        {
            string startCry = re.Behaviors.OfType<IStartCryProvider>().FirstOrDefault()?.StartCryLangKey
                ?? "canevents:arena-start-cry";

            // Round modes show the round's clock while a round runs; 0 = no limit (endless timer).
            var round = re.Behaviors.OfType<IRoundClock>().FirstOrDefault();
            bool roundClock = round != null && round.RoundLive;
            float remaining = roundClock ? round.RoundSecondsLeft
                : re.Unlimited ? 0 : Math.Max(0, re.MaxDurationSeconds - re.ElapsedSeconds);
            float length = roundClock ? round.RoundLength : re.Unlimited ? 0 : re.MaxDurationSeconds;

            return new EventStateUpdateMsg
            {
                EventState = re.EventState,
                GameModeCode = re.Event.Code,
                ArenaName = re.Place?.Name ?? "",
                CountdownSeconds = (int)re.CountdownSeconds,
                TimeRemainingSeconds = remaining,
                MaxDurationSeconds = length,
                EventId = re.Id,
                StartCryLangKey = startCry,
                HoldSeconds = Math.Max(0f, re.HoldEndsAtElapsed - re.ElapsedSeconds),
                HoldLangKey = re.HoldLangKey
            };
        }

        public void SendKillFeed(RunningEvent re, string killerName, string victimName, string method)
        {
            SendToAudience(re, new KillFeedMsg
            {
                KillerName = killerName,
                VictimName = victimName,
                Method = method,
                EventId = re.Id
            });
        }

        public void SendScoreboard(RunningEvent re, ScoreboardData data)
        {
            if (re == null || data == null) return;

            var full = ToScoreboardMsg(re, data);
            ScoreboardUpdateMsg hidden = null;
            foreach (var player in re.HudAudience)
            {
                if (ShowsScoreboardTo(re, player)) serverChannel.SendPacket(full, player);
                else serverChannel.SendPacket(hidden ??= HiddenScoreboardMsg(re), player);
            }
        }

        // For a new watcher, so they don't wait for the next score update.
        public void SendScoreboardTo(RunningEvent re, IServerPlayer player)
        {
            var data = re?.Scorer?.BuildScoreboard();
            if (data == null || player == null) return;
            serverChannel.SendPacket(ShowsScoreboardTo(re, player) ? ToScoreboardMsg(re, data) : HiddenScoreboardMsg(re), player);
        }

        private static bool ShowsScoreboardTo(RunningEvent re, IServerPlayer player)
            => re.Scorer?.ShowsScoreboardTo(player.PlayerUID) != false;

        // Sent instead of skipping, so a table the client already has gets cleared.
        private static ScoreboardUpdateMsg HiddenScoreboardMsg(RunningEvent re)
            => new ScoreboardUpdateMsg { Entries = Array.Empty<ScoreEntryMsg>(), EventId = re.Id, ZoneOwnerTeam = -1 };

        private ScoreboardUpdateMsg ToScoreboardMsg(RunningEvent re, ScoreboardData data)
        {
            var killCounts = data.Kills;
            var deathCounts = data.Deaths;

            // Hidden-role modes: kill counts would reveal roles. Done here so SendScoreboardTo hides them too.
            bool hideScores = re.Scorer?.HidesIndividualScores == true;

            var entries = new List<ScoreEntryMsg>();
            foreach (var plGuid in re.ParticipantsBefore)
            {
                // Bots have no player object, name them from the uid.
                string name = Bots.BotIdentity.IsBot(plGuid)
                    ? Bots.BotIdentity.NameOfUid(plGuid)
                    : (sapi.World.PlayerByUid(plGuid)?.PlayerName);
                if (name == null) continue;   // a real player who went offline mid-match

                int teamId = -1;
                for (int i = 0; i < re.ParticipantsByGroupsBefore.Count; i++)
                {
                    if (re.ParticipantsByGroupsBefore[i].Contains(plGuid))
                    {
                        teamId = i;
                        break;
                    }
                }

                entries.Add(new ScoreEntryMsg
                {
                    PlayerName = name,
                    TeamId = teamId,
                    Kills = !hideScores && killCounts != null && killCounts.ContainsKey(plGuid) ? killCounts[plGuid] : 0,
                    Deaths = !hideScores && deathCounts != null && deathCounts.ContainsKey(plGuid) ? deathCounts[plGuid] : 0,
                    Alive = re.IsActive(plGuid)
                });
            }

            return new ScoreboardUpdateMsg
            {
                Entries = entries.ToArray(),
                EventId = re.Id,
                TeamScores = data.TeamScores,
                ZoneOwnerTeam = data.ZoneOwnerTeam,
                TeamScoreLimit = data.TeamScoreLimit,
                BossName = data.BossName,
                BossHealth = data.BossHealth
            };
        }

        // Empty arrays clear that match's pennants on the client.
        public void SendTeamMarkersTo(RunningEvent re, IServerPlayer player, string[] uids, int[] teams, bool onHead = false)
            => serverChannel.SendPacket(new TeamMarkersMsg { EventId = re.Id, Uids = uids, Teams = teams, OnHead = onHead }, player);

        public void SendCtfCarriers(RunningEvent re, string[] carrierUids, int[] flagTeams)
        {
            SendToAudience(re, new CtfCarriersMsg { CarrierUids = carrierUids, FlagTeams = flagTeams });
        }

        public void SendHgZoneState(RunningEvent re, bool active, int aliveCount, double x, double y, double z,
            float radiusNow, float radiusTarget, float secondsToTarget)
        {
            SendToAudience(re, new HgZoneStateMsg
            {
                Active = active,
                AliveCount = aliveCount,
                X = x,
                Y = y,
                Z = z,
                RadiusNow = radiusNow,
                RadiusTarget = radiusTarget,
                SecondsToTarget = secondsToTarget
            });
        }

        public void SendEventResult(RunningEvent re, string[] winnerNames, ScoreEntryMsg[] finalScores)
        {
            SendToAudience(re, new EventResultMsg
            {
                WinnerNames = winnerNames,
                FinalScores = finalScores,
                DurationSeconds = re.ElapsedSeconds,
                EventId = re.Id
            });
        }
    }
}
