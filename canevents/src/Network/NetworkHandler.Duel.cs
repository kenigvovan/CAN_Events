using System.Collections.Generic;
using System.Linq;
using canevents.src.Duels;
using canevents.src.EventManagers.eventBehavior.Shared;
using canevents.src.EventManagers.events;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace canevents.src.Network
{
    // No privilege needed; DuelManager validates each request.
    public partial class ServerNetworkHandler
    {
        // null when duels are off.
        public DuelManager Duels { get; set; }

        private void RegisterDuelHandlers()
        {
            Handle<DuelChallengeMsg>(ProtocolChannel.Main, false, OnDuelChallenge);
            Handle<DuelResponseMsg>(ProtocolChannel.Main, false, OnDuelResponse);
            Handle<RequestDuelTargetsMsg>(ProtocolChannel.Main, false, (player, _) => SendDuelTargets(player),
                minIntervalMs: 500);
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

            // By id, so a stale popup can't answer a newer challenge.
            var (_, message) = msg.Accept
                ? Duels.Accept(player, msg.ChallengeId)
                : Duels.Decline(player, msg.ChallengeId);
            player.SendMessage(0, message, EnumChatType.Notification);
        }

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

        public void SendDuelClosed(IServerPlayer player, long challengeId)
        {
            if (player == null) return;
            serverChannel.SendPacket(new DuelClosedMsg { ChallengeId = challengeId }, player);
        }

        // Online, free, not self, not in own party.
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
                if (!eventManager.PlayerState.IsFree(other.PlayerUID)) continue;

                names.Add(other.PlayerName);
                sizes.Add(SideSizeOf(other));

                var (w, l, d) = headToHead?.ScoreFor(player.PlayerUID, other.PlayerUID) ?? (0, 0, 0);
                wins.Add(w); losses.Add(l); draws.Add(d);
            }

            int yourSideSize = SideSizeOf(player);

            // Same rules as the challenge check. Set names go through TryGetSelectableSets (hidden roles).
            var modes = Duels.DuellableEvents(yourSideSize).Select(ev => new DuelModeMsg
            {
                Code = ev.Code,
                LoadoutSetNames = TryGetSelectableSets(ev, out var setNames, out _, out _) ? setNames : null
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

        // Party size, or 1.
        private int SideSizeOf(IServerPlayer player)
        {
            var roster = eventManager.PartyRosterOf(player, out _);
            return roster == null || roster.Count == 0 ? 1 : roster.Count;
        }
    }
}
