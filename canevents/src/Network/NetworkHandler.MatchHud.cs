using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers;
using canevents.src.EventManagers.eventBehavior.Shared;
using Vintagestory.API.Server;

namespace canevents.src.Network
{
    // Server → client HUD push: state, kill-feed, scoreboard, result. Called from event behaviors
    // via runningEvent.NetworkHandler.
    public partial class ServerNetworkHandler
    {
        // Every HUD packet goes to the same set: the match's line-up plus anyone observing it. One
        // helper rather than a foreach per send site, so adding a recipient class stays a one-line
        // change and no packet is left reaching only half the audience.
        private void SendToAudience<T>(RunningEvent re, T msg)
        {
            foreach (var player in re.HudAudience) serverChannel.SendPacket(msg, player);
        }

        public void SendStateUpdate(RunningEvent re) => SendToAudience(re, BuildStateUpdate(re));

        /// <summary>Pushes the current match state to one client. Used when someone starts watching
        /// mid-match: their HUD only opens on a state packet, and the next broadcast may be a minute
        /// away.</summary>
        public void SendStateUpdateTo(RunningEvent re, IServerPlayer player)
        {
            if (re == null || player == null) return;
            serverChannel.SendPacket(BuildStateUpdate(re), player);
        }

        /// <summary>
        /// Tells one client that a match is over as far as they are concerned. Sent to an observer who
        /// stopped watching: the client keys its HUD off the state packet and closes on Finished, so
        /// without this their HUD would hang around showing a match they left.
        /// </summary>
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

            return new EventStateUpdateMsg
            {
                EventState = re.EventState,
                GameModeCode = re.Event.Code,
                ArenaName = re.Place?.Name ?? "",
                CountdownSeconds = (int)re.CountdownSeconds,
                TimeRemainingSeconds = Math.Max(0, re.MaxDurationSeconds - re.ElapsedSeconds),
                MaxDurationSeconds = re.MaxDurationSeconds,
                EventId = re.Id,
                StartCryLangKey = startCry
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

        public void SendScoreboard(RunningEvent re, Dictionary<string, int> killCounts, Dictionary<string, int> deathCounts = null, int[] teamScores = null, int zoneOwnerTeam = -1, int teamScoreLimit = 0)
        {
            SendToAudience(re, BuildScoreboard(re, killCounts, deathCounts, teamScores, zoneOwnerTeam, teamScoreLimit));
        }

        /// <summary>The scoreboard as it stands, for one client. Same reason as
        /// <see cref="SendStateUpdateTo"/>: a new watcher should not sit in front of an empty table
        /// until the mode next recomputes scores.</summary>
        public void SendScoreboardTo(RunningEvent re, IServerPlayer player)
        {
            if (re?.Scorer == null || player == null) return;

            var msg = BuildScoreboard(re,
                re.Scorer.GetFinalKillCounts() ?? new Dictionary<string, int>(),
                re.Scorer.GetFinalDeathCounts());
            serverChannel.SendPacket(msg, player);
        }

        private ScoreboardUpdateMsg BuildScoreboard(RunningEvent re, Dictionary<string, int> killCounts,
            Dictionary<string, int> deathCounts, int[] teamScores = null, int zoneOwnerTeam = -1,
            int teamScoreLimit = 0)
        {
            var entries = new List<ScoreEntryMsg>();
            foreach (var plGuid in re.ParticipantsBefore)
            {
                // A bot has no player object, so it is named from its uid - the same way the results
                // table and the body's name tag are, so the three cannot disagree.
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
                    Kills = killCounts != null && killCounts.ContainsKey(plGuid) ? killCounts[plGuid] : 0,
                    Deaths = deathCounts != null && deathCounts.ContainsKey(plGuid) ? deathCounts[plGuid] : 0,
                    Alive = re.IsActive(plGuid)
                });
            }

            return new ScoreboardUpdateMsg
            {
                Entries = entries.ToArray(),
                EventId = re.Id,
                TeamScores = teamScores,
                ZoneOwnerTeam = zoneOwnerTeam,
                TeamScoreLimit = teamScoreLimit
            };
        }

        /// <summary>Tells the audience who is carrying which team's flag, so their clients can
        /// draw it. Match-only, and fires on pickup/capture/death rather than on a timer.</summary>
        public void SendCtfCarriers(RunningEvent re, string[] carrierUids, int[] flagTeams)
        {
            SendToAudience(re, new CtfCarriersMsg { CarrierUids = carrierUids, FlagTeams = flagTeams });
        }

        /// <summary>Pushes the storm-zone state to everyone watching the match (participants,
        /// eliminated spectators and observers alike) for the client-side dome renderer.
        /// See <see cref="HgZoneStateMsg"/>.</summary>
        public void SendHgZoneState(RunningEvent re, HgZoneStateMsg msg) => SendToAudience(re, msg);

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
