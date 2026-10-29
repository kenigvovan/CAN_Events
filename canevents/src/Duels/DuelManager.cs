using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers;
using canevents.src.EventManagers.eventBehavior.Shared;
using canevents.src.EventManagers.events;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace canevents.src.Duels
{
    // Player-to-player challenges. Only holds pending challenges; accepted ones go through
    // CANEventManager.StartMatchFor like tournament matches.
    public class DuelManager
    {
        private readonly ICoreServerAPI sapi;
        private readonly CANEventManager manager;
        private readonly Config config;
        private readonly Log log;

        private readonly Dictionary<long, DuelChallenge> pending = new Dictionary<long, DuelChallenge>();
        private long nextId = 1;

        private long expiryListenerId = -1;

        public DuelManager(ICoreServerAPI sapi, CANEventManager manager, Config config)
        {
            this.sapi = sapi;
            this.manager = manager;
            this.config = config;
            this.log = Log.For(sapi?.World?.Logger, "duel");
            DisabledMutators = new HashSet<string>(
                EventMutators.Normalize(config?.disabledMutators), StringComparer.OrdinalIgnoreCase);
        }

        // Canonical keys; sent to the client so the duel tab hides them.
        public IReadOnlyCollection<string> DisabledMutators { get; }

        public void Start()
        {
            // Tick listener rather than Event.Timer so Dispose can stop it.
            expiryListenerId = sapi.Event.RegisterGameTickListener(_ => ExpireStale(), 1000);
        }

        public void Dispose()
        {
            if (expiryListenerId >= 0) sapi?.Event.UnregisterGameTickListener(expiryListenerId);
            expiryListenerId = -1;
        }

        private long NowMs => sapi.World.ElapsedMilliseconds;

        public (bool ok, string message) Challenge(IServerPlayer challenger, string targetName,
            DuelKind kind, string wagerPacked = null, string eventCode = null, string[] mutators = null)
        {
            if (!config.duelsEnabled) return (false, Lang.Get("canevents:duel-disabled"));
            if (challenger == null) return (false, Lang.Get("canevents:duel-failed"));

            var target = FindOnlineByName(targetName);
            if (target == null) return (false, Lang.Get("canevents:duel-no-such-player", targetName));

            if (target.PlayerUID == challenger.PlayerUID)
                return (false, Lang.Get("canevents:duel-not-yourself"));

            // Sides are whole parties, only the leader can challenge (same as AddToQueue).
            if (!TryBuildSide(challenger, out var challengerSide, out string sideError))
                return (false, sideError);
            if (!TryBuildSide(target, out var targetSide, out sideError))
                return (false, sideError);

            if (challengerSide.Contains(target.PlayerUID))
                return (false, Lang.Get("canevents:duel-same-party"));

            if (challengerSide.Count != targetSide.Count)
                return (false, Lang.Get("canevents:duel-side-mismatch",
                    challengerSide.Count, targetSide.Count));

            // Whole party must be free, not just the leaders.
            if (!AllAvailable(challengerSide, out string busyName))
                return (false, Lang.Get("canevents:duel-side-busy", busyName));
            if (!AllAvailable(targetSide, out busyName))
                return (false, Lang.Get("canevents:duel-side-busy", busyName));

            bool modePinned = !string.IsNullOrWhiteSpace(eventCode);
            var template = modePinned
                ? FindDuelEventByCode(eventCode, challengerSide.Count)
                : FindDuelEvent(challengerSide.Count);
            if (template == null)
                return (false, modePinned
                    ? Lang.Get("canevents:duel-bad-mode", eventCode, challengerSide.Count)
                    : Lang.Get("canevents:duel-no-event", challengerSide.Count));

            var parsedMutators = EventMutators.ParseAll(mutators, out var badMutators);
            if (badMutators.Count > 0)
                return (false, Lang.Get("canevents:duel-bad-mutator", badMutators[0]));

            // Reported by key, since that's what disabledMutators uses.
            var offLimits = parsedMutators.Keys.FirstOrDefault(DisabledMutators.Contains);
            if (offLimits != null)
                return (false, Lang.Get("canevents:duel-mutator-disabled", offLimits));

            if (parsedMutators.TryGetValue(EventMutators.Loadout, out var forcedSet))
            {
                var provider = template.Behaviors.OfType<ILoadoutProvider>().FirstOrDefault();
                var setNames = provider?.GetLoadoutSetNames(template.Code, manager.LoadoutManager);
                if (setNames == null
                    || !setNames.Contains(forcedSet, StringComparer.OrdinalIgnoreCase))
                    return (false, Lang.Get("canevents:duel-bad-loadout", forcedSet, template.Code));
            }

            // Check affordability now, not after the opponent accepts.
            List<RewardItem> wager = null;
            if (kind == DuelKind.Wager)
            {
                if (!TryReadWager(challenger, wagerPacked, out wager, out string wagerError))
                    return (false, wagerError);
            }

            // One challenge per person; a new one replaces the old.
            DropChallengesInvolving(challengerSide, "canevents:duel-cancelled-replaced");
            DropChallengesInvolving(targetSide, "canevents:duel-cancelled-replaced");

            var challenge = new DuelChallenge
            {
                Id = nextId++,
                ChallengerUid = challenger.PlayerUID,
                TargetUid = target.PlayerUID,
                ChallengerSide = challengerSide,
                TargetSide = targetSide,
                Kind = kind,
                Wager = wager,
                EventCode = template.Code,
                ModePinned = modePinned,
                Mutators = EventMutators.Pack(parsedMutators).ToList(),
                ExpiresAtMs = NowMs + Math.Max(10, config.duelInviteTimeoutSeconds) * 1000L
            };
            pending[challenge.Id] = challenge;

            PromptTarget(challenge, target, challenger);

            log.Notification("{0} challenged {1} ({2}, {3}v{3})",
                challenger.PlayerName, target.PlayerName, kind, challengerSide.Count);

            var (wins, losses, draws) = manager.HeadToHead
                ?.ScoreFor(challenger.PlayerUID, target.PlayerUID) ?? (0, 0, 0);
            string sent = Lang.Get("canevents:duel-sent", target.PlayerName);
            if (wins + losses + draws > 0)
                sent += " " + Lang.Get("canevents:duel-headtohead", target.PlayerName, wins, losses, draws);

            return (true, sent);
        }

        // Non-zero challengeId must match exactly, so a stale popup can't accept newer terms.
        public (bool ok, string message) Accept(IServerPlayer player, long challengeId = 0)
        {
            var challenge = PendingFor(player?.PlayerUID, challengeId);
            if (challenge == null) return (false, Lang.Get("canevents:duel-nothing-pending"));

            if (challenge.TargetUid != player.PlayerUID)
                return (false, Lang.Get("canevents:duel-not-yours-to-answer"));

            pending.Remove(challenge.Id);
            CloseThisPrompt(challenge);

            return StartDuel(challenge);
        }

        // Either side can decline, so the challenger can withdraw.
        public (bool ok, string message) Decline(IServerPlayer player, long challengeId = 0)
        {
            var challenge = PendingFor(player?.PlayerUID, challengeId);
            if (challenge == null) return (false, Lang.Get("canevents:duel-nothing-pending"));

            pending.Remove(challenge.Id);
            CloseThisPrompt(challenge);

            bool byTarget = challenge.TargetUid == player.PlayerUID;
            NotifySide(byTarget ? challenge.ChallengerSide : challenge.TargetSide,
                Lang.Get("canevents:duel-declined-by", player.PlayerName));

            return (true, Lang.Get("canevents:duel-declined"));
        }

        private (bool ok, string message) StartDuel(DuelChallenge challenge)
        {
            // Recheck, things may have changed since the challenge was sent.
            if (!AllAvailable(challenge.ChallengerSide, out string busyName)
                || !AllAvailable(challenge.TargetSide, out busyName))
            {
                NotifyBoth(challenge, Lang.Get("canevents:duel-side-busy", busyName));
                return (false, Lang.Get("canevents:duel-side-busy", busyName));
            }

            // Pinned mode gone = no duel, never fall back to another mode.
            var template = challenge.ModePinned
                ? FindDuelEventByCode(challenge.EventCode, challenge.SideSize)
                : FindDuelEvent(challenge.SideSize);
            if (template == null)
            {
                NotifyBoth(challenge, Lang.Get("canevents:duel-no-event", challenge.SideSize));
                return (false, Lang.Get("canevents:duel-no-event", challenge.SideSize));
            }

            var ev = BuildDuelEvent(template, challenge);

            // Charged on accept, not on challenge, so nothing sits in escrow for unaccepted duels.
            if (challenge.Kind == DuelKind.Wager && !ChargeBothStakes(challenge, ev.Code, out string feeError))
            {
                NotifyBoth(challenge, feeError);
                return (false, feeError);
            }

            var teams = new List<IReadOnlyList<string>> { challenge.ChallengerSide, challenge.TargetSide };

            var re = manager.StartMatchFor(ev, teams, null, null, out string reason);
            if (re == null)
            {
                if (challenge.Kind == DuelKind.Wager) RefundBothStakes(challenge);

                NotifyBoth(challenge, Lang.Get("canevents:duel-start-failed", reason ?? "?"));
                return (false, Lang.Get("canevents:duel-start-failed", reason ?? "?"));
            }

            // Duels are never rated, see DuelKind.
            re.SuppressRating = true;
            if (challenge.Kind == DuelKind.Friendly) re.SuppressRewards = true;

            log.Notification("Duel started: {0} vs {1} ({2}, event {3}, match {4})",
                NameOf(challenge.ChallengerUid), NameOf(challenge.TargetUid),
                challenge.Kind, ev.Code, re.Id);

            return (true, Lang.Get("canevents:duel-started", NameOf(challenge.TargetUid)));
        }

        // Cloned so duel terms don't leak into the queue's event.
        private CANEvent BuildDuelEvent(CANEvent template, DuelChallenge challenge)
        {
            var ev = template.Clone();
            ev.Rated = false;

            foreach (var kv in EventMutators.ParseAll(challenge.Mutators, out _))
                ev.Mutators[kv.Key] = kv.Value;

            if (challenge.Kind == DuelKind.Friendly)
            {
                ev.Reward = null;
                ev.EntryFee = null;
                return ev;
            }

            // Stake replaces the event fee, no rake.
            ev.Reward = null;
            ev.EntryFee = new EntryFeeSpec { Items = challenge.Wager, RakePercent = 0 };
            return ev;
        }

        // Only the leader pays, so only the challenger is checked.
        private bool TryReadWager(IServerPlayer challenger, string packed,
            out List<RewardItem> wager, out string error)
        {
            wager = null;
            error = null;

            wager = ItemSetUtil.Parse(packed);
            if (wager == null || wager.Count == 0)
            {
                error = Lang.Get("canevents:duel-wager-empty");
                return false;
            }

            // Coalesce so "gear:4;gear:4" is checked as 8 (same as EntryFeeEscrow.Charge).
            if (!InventoryUtil.Has(challenger, ItemSetUtil.Coalesce(wager)))
            {
                error = Lang.Get("canevents:duel-wager-cannot-afford",
                    ItemSetUtil.Pack(wager) ?? "?");
                return false;
            }

            return true;
        }

        // All or nothing: if the second leader can't pay, the first is refunded.
        private bool ChargeBothStakes(DuelChallenge challenge, string eventCode, out string error)
        {
            error = null;

            var challengerLeader = sapi.World.PlayerByUid(challenge.ChallengerSide[0]) as IServerPlayer;
            var targetLeader = sapi.World.PlayerByUid(challenge.TargetSide[0]) as IServerPlayer;

            if (challengerLeader == null || targetLeader == null)
            {
                error = Lang.Get("canevents:duel-wager-side-gone");
                return false;
            }

            if (!EntryFeeEscrow.Charge(challengerLeader, eventCode, challenge.Wager))
            {
                error = Lang.Get("canevents:duel-wager-cannot-afford-named", challengerLeader.PlayerName);
                return false;
            }

            if (!EntryFeeEscrow.Charge(targetLeader, eventCode, challenge.Wager))
            {
                manager.RewardDistributor?.RefundEntryFeeEscrow(challengerLeader);
                error = Lang.Get("canevents:duel-wager-cannot-afford-named", targetLeader.PlayerName);
                return false;
            }

            return true;
        }

        private void RefundBothStakes(DuelChallenge challenge)
        {
            foreach (var uid in new[] { challenge.ChallengerSide[0], challenge.TargetSide[0] })
                if (sapi.World.PlayerByUid(uid) is IServerPlayer leader)
                    manager.RewardDistributor?.RefundEntryFeeEscrow(leader);
        }

        // Enabled events with 2+ sides that fit this side size. Also used by the duel tab mode picker.
        public IEnumerable<CANEvent> DuellableEvents(int sideSize)
        {
            foreach (var ev in manager.Events)
            {
                var assembling = ev?.AssemblingEB;
                if (assembling == null) continue;
                if (assembling.MaxPartiesCount < 2) continue;
                if (sideSize < assembling.MinPartySize || sideSize > assembling.MaxPartySize) continue;
                if (!manager.IsEventEnabled(ev.Code)) continue;
                yield return ev;
            }
        }

        // Prefers unrated; a rated event is fine too since rating is suppressed anyway.
        private CANEvent FindDuelEvent(int sideSize)
        {
            CANEvent ratedFallback = null;
            foreach (var ev in DuellableEvents(sideSize))
            {
                if (!ev.Rated) return ev;
                ratedFallback ??= ev;
            }
            return ratedFallback;
        }

        private CANEvent FindDuelEventByCode(string code, int sideSize)
            => DuellableEvents(sideSize).FirstOrDefault(ev => ev.CodeEquals(code));

        // Party if leader, solo if no party, false if a non-leader member.
        private bool TryBuildSide(IServerPlayer player, out List<string> side, out string error)
        {
            error = null;
            side = null;

            var roster = manager.PartyRosterOf(player, out bool isLeader);
            if (roster == null || roster.Count <= 1)
            {
                side = new List<string> { player.PlayerUID };
                return true;
            }

            if (!isLeader)
            {
                error = Lang.Get("canevents:duel-party-leader-only", player.PlayerName);
                return false;
            }

            // Leader first, wagers use side[0].
            side = new List<string> { player.PlayerUID };
            side.AddRange(roster.Where(uid => uid != player.PlayerUID));
            return true;
        }

        private bool AllAvailable(IEnumerable<string> side, out string busyName)
        {
            busyName = null;
            foreach (var uid in side)
            {
                if (manager.PlayerState.IsFree(uid)) continue;
                busyName = NameOf(uid);
                return false;
            }
            return true;
        }

        private void ExpireStale()
        {
            if (pending.Count == 0) return;

            long now = NowMs;
            foreach (var challenge in pending.Values.Where(c => c.ExpiresAtMs <= now).ToList())
            {
                pending.Remove(challenge.Id);
                CloseThisPrompt(challenge);
                NotifyBoth(challenge, Lang.Get("canevents:duel-expired"));
            }
        }

        public void OnDisconnect(string playerUid)
        {
            foreach (var challenge in pending.Values.Where(c => c.Involves(playerUid)).ToList())
            {
                pending.Remove(challenge.Id);
                CloseThisPrompt(challenge);
                NotifyBoth(challenge, Lang.Get("canevents:duel-cancelled-left", NameOf(playerUid)));
            }
        }

        private void DropChallengesInvolving(IEnumerable<string> uids, string reasonKey)
        {
            var set = new HashSet<string>(uids);
            foreach (var challenge in pending.Values.Where(c => c.ChallengerSide.Any(set.Contains)
                                                             || c.TargetSide.Any(set.Contains)).ToList())
            {
                pending.Remove(challenge.Id);
                CloseThisPrompt(challenge);
                NotifyBoth(challenge, Lang.Get(reasonKey));
            }
        }

        private DuelChallenge PendingFor(string uid, long challengeId = 0)
        {
            if (string.IsNullOrEmpty(uid)) return null;
            if (challengeId > 0)
                return pending.TryGetValue(challengeId, out var byId)
                    && (byId.ChallengerUid == uid || byId.TargetUid == uid) ? byId : null;
            return pending.Values.FirstOrDefault(c => c.ChallengerUid == uid || c.TargetUid == uid);
        }

        private void PromptTarget(DuelChallenge challenge, IServerPlayer target, IServerPlayer challenger)
        {
            manager.NetworkHandler?.SendDuelPrompt(target, challenge, challenger.PlayerName);

            // Also in chat, the popup can be closed but the command still works.
            target.SendMessage(0, Lang.Get("canevents:duel-prompt-chat", challenger.PlayerName,
                config.duelInviteTimeoutSeconds), EnumChatType.Notification);
        }

        private void CloseThisPrompt(DuelChallenge challenge)
        {
            foreach (var uid in challenge.TargetSide.Concat(challenge.ChallengerSide))
                if (sapi.World.PlayerByUid(uid) is IServerPlayer player)
                    manager.NetworkHandler?.SendDuelClosed(player, challenge.Id);
        }

        private void NotifyBoth(DuelChallenge challenge, string message)
        {
            NotifySide(challenge.ChallengerSide, message);
            NotifySide(challenge.TargetSide, message);
        }

        private void NotifySide(IEnumerable<string> side, string message)
        {
            foreach (var uid in side)
                if (sapi.World.PlayerByUid(uid) is IServerPlayer player)
                    player.SendMessage(0, message, EnumChatType.Notification);
        }

        private IServerPlayer FindOnlineByName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            return sapi.World.AllOnlinePlayers
                .OfType<IServerPlayer>()
                .FirstOrDefault(p => string.Equals(p.PlayerName, name.Trim(),
                    StringComparison.OrdinalIgnoreCase));
        }

        private string NameOf(string uid) => RunningEvent.NameOf(sapi, uid);
    }
}
