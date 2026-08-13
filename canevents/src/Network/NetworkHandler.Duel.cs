using System.Collections.Generic;
using System.Linq;
using canevents.src.Duels;
using canevents.src.EventManagers.eventBehavior.Shared;
using canevents.src.EventManagers.events;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace canevents.src.Network
{
    // Duels: challenge, answer, and the list of who can be challenged. Open to every player - a duel
    // needs no privilege, and each request is validated against server state by DuelManager.
    public partial class ServerNetworkHandler
    {
        /// <summary>Set from canevents.cs once the driver exists; null on a server with duels off.</summary>
        public DuelManager Duels { get; set; }

        private void RegisterDuelHandlers()
        {
            SetHandler<DuelChallengeMsg>(OnDuelChallenge);
            SetHandler<DuelResponseMsg>(OnDuelResponse);
            SetHandler<RequestDuelTargetsMsg>((player, _) => SendDuelTargets(player));
        }

        private void OnDuelChallenge(IServerPlayer player, DuelChallengeMsg msg)
        {
            if (Duels == null || msg == null) return;

            var (_, message) = Duels.Challenge(player, msg.TargetName, msg.Kind, msg.WagerItems,
                msg.EventCode, msg.Mutators);
            player.SendMessage(0, message, EnumChatType.Notification);
        }

        private void OnDuelResponse(IServerPlayer player, DuelResponseMsg msg)
        {
            if (Duels == null || msg == null) return;

            var (_, message) = msg.Accept ? Duels.Accept(player) : Duels.Decline(player);
            player.SendMessage(0, message, EnumChatType.Notification);
        }

        /// <summary>Shows the challenge prompt to one player.</summary>
        public void SendDuelPrompt(IServerPlayer player, DuelChallenge challenge, string challengerName)
        {
            if (player == null || challenge == null) return;

            var (wins, losses, draws) = eventManager.HeadToHead
                ?.ScoreFor(player.PlayerUID, challenge.ChallengerUid) ?? (0, 0, 0);

            serverChannel.SendPacket(new DuelPromptMsg
            {
                ChallengeId = challenge.Id,
                ChallengerName = challengerName,
                Kind = challenge.Kind,
                SideSize = challenge.SideSize,
                TimeoutSeconds = System.Math.Max(0,
                    (challenge.ExpiresAtMs - sapi.World.ElapsedMilliseconds) / 1000f),
                WagerItems = ItemSetUtil.Pack(challenge.Wager),
                EventCode = challenge.EventCode,
                Mutators = challenge.Mutators?.ToArray(),
                YourWinsVsChallenger = wins,
                ChallengerWinsVsYou = losses,
                DrawsVsChallenger = draws
            }, player);
        }

        /// <summary>Takes the prompt off one player's screen.</summary>
        public void SendDuelClosed(IServerPlayer player, long challengeId)
        {
            if (player == null) return;
            serverChannel.SendPacket(new DuelClosedMsg { ChallengeId = challengeId }, player);
        }

        /// <summary>
        /// Who this player could challenge right now: online, not busy, not themselves, not in their
        /// own party. Side sizes ride along so the browser can show "2v2" and grey out a row whose
        /// side does not match the viewer's.
        /// </summary>
        private void SendDuelTargets(IServerPlayer player)
        {
            if (Duels == null || player == null) return;

            var names = new List<string>();
            var sizes = new List<int>();
            var wins = new List<int>();
            var losses = new List<int>();
            var draws = new List<int>();

            var ownParty = eventManager.PartyRosterOf(player, out _);
            var ownSet = ownParty != null ? new HashSet<string>(ownParty) : new HashSet<string>();
            var headToHead = eventManager.HeadToHead;

            foreach (var other in sapi.World.AllOnlinePlayers.OfType<IServerPlayer>())
            {
                if (other.PlayerUID == player.PlayerUID) continue;
                if (ownSet.Contains(other.PlayerUID)) continue;
                if (eventManager.IsBusy(other.PlayerUID)) continue;

                names.Add(other.PlayerName);
                sizes.Add(SideSizeOf(other));

                var (w, l, d) = headToHead?.ScoreFor(player.PlayerUID, other.PlayerUID) ?? (0, 0, 0);
                wins.Add(w); losses.Add(l); draws.Add(d);
            }

            int yourSideSize = SideSizeOf(player);

            // The mode picker's contents, computed by the same rules that will judge the challenge -
            // a mode offered here can never bounce as "bad mode" later.
            var modes = Duels.DuellableEvents(yourSideSize).Select(ev => new DuelModeMsg
            {
                Code = ev.Code,
                LoadoutSetNames = ev.Behaviors.OfType<ILoadoutProvider>().FirstOrDefault()
                    ?.GetLoadoutSetNames(ev.Code, eventManager.LoadoutManager)
            }).ToArray();

            serverChannel.SendPacket(new DuelTargetListMsg
            {
                PlayerNames = names.ToArray(),
                SideSizes = sizes.ToArray(),
                YourSideSize = yourSideSize,
                Modes = modes,
                DisabledMutators = Duels.DisabledMutators.ToArray(),
                WinsVs = wins.ToArray(),
                LossesVs = losses.ToArray(),
                DrawsVs = draws.ToArray()
            }, player);
        }

        // A player brings their whole party, or just themselves.
        private int SideSizeOf(IServerPlayer player)
        {
            var roster = eventManager.PartyRosterOf(player, out _);
            return roster == null || roster.Count == 0 ? 1 : roster.Count;
        }
    }
}
