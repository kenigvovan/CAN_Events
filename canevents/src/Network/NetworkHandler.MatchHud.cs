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
        public void SendStateUpdate(RunningEvent re)
        {
            string startCry = re.Behaviors.OfType<IStartCryProvider>().FirstOrDefault()?.StartCryLangKey
                ?? "canevents:arena-start-cry";

            var msg = new EventStateUpdateMsg
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

            foreach (var plGuid in re.ParticipantsBefore)
            {
                var player = sapi.World.PlayerByUid(plGuid) as IServerPlayer;
                if (player != null)
                {
                    serverChannel.SendPacket(msg, player);
                }
            }
        }

        public void SendKillFeed(RunningEvent re, string killerName, string victimName, string method)
        {
            var msg = new KillFeedMsg
            {
                KillerName = killerName,
                VictimName = victimName,
                Method = method,
                EventId = re.Id
            };

            foreach (var plGuid in re.ParticipantsBefore)
            {
                var player = sapi.World.PlayerByUid(plGuid) as IServerPlayer;
                if (player != null)
                {
                    serverChannel.SendPacket(msg, player);
                }
            }
        }

        public void SendScoreboard(RunningEvent re, Dictionary<string, int> killCounts, Dictionary<string, int> deathCounts = null, int[] teamScores = null, int zoneOwnerTeam = -1, int teamScoreLimit = 0)
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
                    Kills = killCounts.ContainsKey(plGuid) ? killCounts[plGuid] : 0,
                    Deaths = deathCounts != null && deathCounts.ContainsKey(plGuid) ? deathCounts[plGuid] : 0,
                    Alive = re.IsActive(plGuid)
                });
            }

            var msg = new ScoreboardUpdateMsg
            {
                Entries = entries.ToArray(),
                EventId = re.Id,
                TeamScores = teamScores,
                ZoneOwnerTeam = zoneOwnerTeam,
                TeamScoreLimit = teamScoreLimit
            };

            foreach (var plGuid in re.ParticipantsBefore)
            {
                var player = sapi.World.PlayerByUid(plGuid) as IServerPlayer;
                if (player != null)
                {
                    serverChannel.SendPacket(msg, player);
                }
            }
        }

        /// <summary>Tells the participants who is carrying which team's flag, so their clients can
        /// draw it. Match-only, and fires on pickup/capture/death rather than on a timer.</summary>
        public void SendCtfCarriers(RunningEvent re, string[] carrierUids, int[] flagTeams)
        {
            var msg = new CtfCarriersMsg { CarrierUids = carrierUids, FlagTeams = flagTeams };

            foreach (var plGuid in re.ParticipantsBefore)
            {
                if (sapi.World.PlayerByUid(plGuid) is IServerPlayer player)
                    serverChannel.SendPacket(msg, player);
            }
        }

        /// <summary>Pushes the storm-zone state to everyone in the match (incl. eliminated
        /// spectators) for the client-side dome renderer. See <see cref="HgZoneStateMsg"/>.</summary>
        public void SendHgZoneState(RunningEvent re, HgZoneStateMsg msg)
        {
            foreach (var plGuid in re.ParticipantsBefore)
            {
                if (sapi.World.PlayerByUid(plGuid) is IServerPlayer player)
                    serverChannel.SendPacket(msg, player);
            }
        }

        public void SendEventResult(RunningEvent re, string[] winnerNames, ScoreEntryMsg[] finalScores)
        {
            var msg = new EventResultMsg
            {
                WinnerNames = winnerNames,
                FinalScores = finalScores,
                DurationSeconds = re.ElapsedSeconds,
                EventId = re.Id
            };

            foreach (var plGuid in re.ParticipantsBefore)
            {
                var player = sapi.World.PlayerByUid(plGuid) as IServerPlayer;
                if (player != null)
                {
                    serverChannel.SendPacket(msg, player);
                }
            }
        }
    }
}
