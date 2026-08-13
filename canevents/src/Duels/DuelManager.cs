using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers;
using canevents.src.EventManagers.events;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace canevents.src.Duels
{
    /// <summary>
    /// Player-to-player challenges. Until now the only way into a match was the queue, which decides
    /// who you play; two people standing next to each other had no way to just fight.
    ///
    /// A driver of its own rather than more surface on CANEventManager (already 1600 lines across
    /// five files). It owns nothing but the outstanding challenges - once one is accepted the match
    /// is handed to <see cref="CANEventManager.StartMatchFor"/>, the same entry the tournament driver
    /// uses, and everything after that is the ordinary match machinery.
    /// </summary>
    public class DuelManager
    {
        private readonly ICoreServerAPI sapi;
        private readonly CANEventManager manager;
        private readonly Config config;
        private readonly Log log;

        // Outstanding challenges by id. Small by nature: one per pair, and they lapse in a minute.
        private readonly Dictionary<long, DuelChallenge> pending = new Dictionary<long, DuelChallenge>();
        private long nextId = 1;

        private long expiryListenerId = -1;

        public DuelManager(ICoreServerAPI sapi, CANEventManager manager, Config config)
        {
            this.sapi = sapi;
            this.manager = manager;
            this.config = config;
            this.log = Log.For(sapi?.World?.Logger, "duel");
        }

        public void Start()
        {
            // One second is fine: the deadline is a minute, and nobody minds a challenge lapsing a
            // beat late. Registered rather than Event.Timer so a mod reload can stop it.
            expiryListenerId = sapi.Event.RegisterGameTickListener(_ => ExpireStale(), 1000);
        }

        public void Dispose()
        {
            if (expiryListenerId >= 0) sapi?.Event.UnregisterGameTickListener(expiryListenerId);
            expiryListenerId = -1;
        }

        /// <summary>Server clock the deadlines are measured against.</summary>
        private long NowMs => sapi.World.ElapsedMilliseconds;

        // Sending a challenge

        /// <summary>
        /// Creates a challenge from one player to another and prompts the target. Returns a localized
        /// message either way, for the command and the packet handler to relay.
        /// </summary>
        public (bool ok, string message) Challenge(IServerPlayer challenger, string targetName,
            DuelKind kind, string wagerPacked = null)
        {
            if (!config.duelsEnabled) return (false, Lang.Get("canevents:duel-disabled"));
            if (challenger == null) return (false, Lang.Get("canevents:duel-failed"));

            var target = FindOnlineByName(targetName);
            if (target == null) return (false, Lang.Get("canevents:duel-no-such-player", targetName));

            if (target.PlayerUID == challenger.PlayerUID)
                return (false, Lang.Get("canevents:duel-not-yourself"));

            // Sides are whole parties, and only a leader may commit their party to a fight - the same
            // rule the queue enforces in AddToQueue.
            if (!TryBuildSide(challenger, out var challengerSide, out string sideError))
                return (false, sideError);
            if (!TryBuildSide(target, out var targetSide, out sideError))
                return (false, sideError);

            // Being in the same party is not a duel, it is a misunderstanding.
            if (challengerSide.Contains(target.PlayerUID))
                return (false, Lang.Get("canevents:duel-same-party"));

            if (challengerSide.Count != targetSide.Count)
                return (false, Lang.Get("canevents:duel-side-mismatch",
                    challengerSide.Count, targetSide.Count));

            // Everyone has to be free, not just the two who are talking: a party member already in a
            // match cannot be dragged into another one.
            if (!AllAvailable(challengerSide, out string busyName))
                return (false, Lang.Get("canevents:duel-side-busy", busyName));
            if (!AllAvailable(targetSide, out busyName))
                return (false, Lang.Get("canevents:duel-side-busy", busyName));

            // Refuse early rather than at accept time: an event that does not exist for this side
            // size means the challenge could never have been played.
            var template = FindDuelEvent(challengerSide.Count);
            if (template == null)
                return (false, Lang.Get("canevents:duel-no-event", challengerSide.Count));

            // Stake is settled before the challenge goes out: being told "you cannot afford this"
            // after the opponent already accepted wastes their time and looks like a bug.
            List<RewardItem> wager = null;
            if (kind == DuelKind.Wager)
            {
                if (!TryReadWager(challenger, wagerPacked, out wager, out string wagerError))
                    return (false, wagerError);
            }

            // One challenge per person at a time. A repeat replaces the old one rather than stacking,
            // so a player cannot be buried under prompts.
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
                ExpiresAtMs = NowMs + Math.Max(10, config.duelInviteTimeoutSeconds) * 1000L
            };
            pending[challenge.Id] = challenge;

            PromptTarget(challenge, target, challenger);

            log.Notification("{0} challenged {1} ({2}, {3}v{3})",
                challenger.PlayerName, target.PlayerName, kind, challengerSide.Count);

            // The challenged player sees the head-to-head score on their prompt; the challenger
            // gets the same courtesy here, the only message they receive.
            var (wins, losses, draws) = manager.HeadToHead
                ?.ScoreFor(challenger.PlayerUID, target.PlayerUID) ?? (0, 0, 0);
            string sent = Lang.Get("canevents:duel-sent", target.PlayerName);
            if (wins + losses + draws > 0)
                sent += " " + Lang.Get("canevents:duel-headtohead", target.PlayerName, wins, losses, draws);

            return (true, sent);
        }

        // Answering

        /// <summary>Accepts the challenge waiting for this player, and starts the match.</summary>
        public (bool ok, string message) Accept(IServerPlayer player)
        {
            var challenge = PendingFor(player?.PlayerUID);
            if (challenge == null) return (false, Lang.Get("canevents:duel-nothing-pending"));

            // Only the challenged leader answers: their party is what is being committed.
            if (challenge.TargetUid != player.PlayerUID)
                return (false, Lang.Get("canevents:duel-not-yours-to-answer"));

            pending.Remove(challenge.Id);
            CloseThisPrompt(challenge);

            return StartDuel(challenge);
        }

        /// <summary>Declines it. Works for either side, so the challenger can take theirs back.</summary>
        public (bool ok, string message) Decline(IServerPlayer player)
        {
            var challenge = PendingFor(player?.PlayerUID);
            if (challenge == null) return (false, Lang.Get("canevents:duel-nothing-pending"));

            pending.Remove(challenge.Id);
            CloseThisPrompt(challenge);

            bool byTarget = challenge.TargetUid == player.PlayerUID;
            NotifySide(byTarget ? challenge.ChallengerSide : challenge.TargetSide,
                Lang.Get("canevents:duel-declined-by", player.PlayerName));

            return (true, Lang.Get("canevents:duel-declined"));
        }

        // Starting

        private (bool ok, string message) StartDuel(DuelChallenge challenge)
        {
            // Re-checked at accept time, not trusted from creation: a minute is long enough for
            // someone to join a match, leave their party or log off.
            if (!AllAvailable(challenge.ChallengerSide, out string busyName)
                || !AllAvailable(challenge.TargetSide, out busyName))
            {
                NotifyBoth(challenge, Lang.Get("canevents:duel-side-busy", busyName));
                return (false, Lang.Get("canevents:duel-side-busy", busyName));
            }

            var template = FindDuelEvent(challenge.SideSize);
            if (template == null)
            {
                NotifyBoth(challenge, Lang.Get("canevents:duel-no-event", challenge.SideSize));
                return (false, Lang.Get("canevents:duel-no-event", challenge.SideSize));
            }

            var ev = BuildDuelEvent(template, challenge);

            // Stakes before the match: both sides pay or neither does. Done here rather than at
            // challenge time because the challenge may never be accepted, and money must not sit in
            // escrow for a fight that is not happening.
            if (challenge.Kind == DuelKind.Wager && !ChargeBothStakes(challenge, ev.Code, out string feeError))
            {
                NotifyBoth(challenge, feeError);
                return (false, feeError);
            }

            var teams = new List<IReadOnlyList<string>> { challenge.ChallengerSide, challenge.TargetSide };

            var re = manager.StartMatchFor(ev, teams, null, null, out string reason);
            if (re == null)
            {
                // The match never started, so nobody owes anything - hand the stakes straight back.
                if (challenge.Kind == DuelKind.Wager) RefundBothStakes(challenge);

                NotifyBoth(challenge, Lang.Get("canevents:duel-start-failed", reason ?? "?"));
                return (false, Lang.Get("canevents:duel-start-failed", reason ?? "?"));
            }

            // A duel never moves the ladder, whatever event it borrowed. See DuelKind for why.
            re.SuppressRating = true;
            if (challenge.Kind == DuelKind.Friendly) re.SuppressRewards = true;

            log.Notification("Duel started: {0} vs {1} ({2}, event {3}, match {4})",
                NameOf(challenge.ChallengerUid), NameOf(challenge.TargetUid),
                challenge.Kind, ev.Code, re.Id);

            return (true, Lang.Get("canevents:duel-started", NameOf(challenge.TargetUid)));
        }

        /// <summary>
        /// A throwaway copy of the event, tuned for this duel. Cloned rather than used directly so a
        /// duel's terms never leak into the queue's copy of the same event - the same reason the
        /// debug bot match clones.
        /// </summary>
        private CANEvent BuildDuelEvent(CANEvent template, DuelChallenge challenge)
        {
            var ev = template.Clone();
            ev.Rated = false;

            if (challenge.Kind == DuelKind.Friendly)
            {
                ev.Reward = null;
                ev.EntryFee = null;
                return ev;
            }

            // Wager: the stake replaces whatever fee the borrowed event carried, and the whole pot
            // goes to the winner - the house takes no cut of a fight between two players.
            ev.Reward = null;
            ev.EntryFee = new EntryFeeSpec { Items = challenge.Wager, RakePercent = 0 };
            return ev;
        }

        // Stakes

        /// <summary>
        /// Reads a stake the challenger typed or picked, and refuses one they cannot cover. Only the
        /// leader of each side pays, so this is checked against them alone.
        /// </summary>
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

            // Coalesced first: "gear:4;gear:4" must be judged as eight, or the shortfall is minted
            // at payout. Same reasoning as EntryFeeEscrow.Charge.
            if (!InventoryUtil.Has(challenger, ItemSetUtil.Coalesce(wager)))
            {
                error = Lang.Get("canevents:duel-wager-cannot-afford",
                    ItemSetUtil.Pack(wager) ?? "?");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Takes one stake from each side's leader, all or nothing. If the second leader cannot pay,
        /// the first is refunded before anything else happens - a duel that never starts must not
        /// leave one player lighter.
        /// </summary>
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

        /// <summary>Hands both stakes back. Used when the match failed to start after they were taken.</summary>
        private void RefundBothStakes(DuelChallenge challenge)
        {
            foreach (var uid in new[] { challenge.ChallengerSide[0], challenge.TargetSide[0] })
                if (sapi.World.PlayerByUid(uid) is IServerPlayer leader)
                    manager.RewardDistributor?.RefundEntryFeeEscrow(leader);
        }

        /// <summary>
        /// An event these sides can actually play: right side size, at least two sides, and unrated
        /// by preference. Searched rather than hardcoded to "1x1"/"2x2" so adding a mode to the
        /// config is enough to make it duellable.
        /// </summary>
        private CANEvent FindDuelEvent(int sideSize)
        {
            CANEvent ratedFallback = null;

            foreach (var ev in manager.Events)
            {
                var assembling = ev?.AssemblingEB;
                if (assembling == null) continue;
                if (assembling.MaxPartiesCount < 2) continue;
                if (sideSize < assembling.MinPartySize || sideSize > assembling.MaxPartySize) continue;
                if (!manager.IsEventEnabled(ev.Code)) continue;

                if (!ev.Rated) return ev;
                ratedFallback ??= ev;
            }

            // Only a rated mode exists at this size. Borrow its shape; the rating is suppressed on
            // the running match either way.
            return ratedFallback;
        }

        // Sides

        /// <summary>
        /// This player's side: their party if they lead one, otherwise just themselves. False when
        /// they are in a party they do not lead - committing someone else's party to a fight is the
        /// leader's call, exactly as it is for the queue.
        /// </summary>
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

            // Leader first: a wager is paid and refunded by whoever leads.
            side = new List<string> { player.PlayerUID };
            side.AddRange(roster.Where(uid => uid != player.PlayerUID));
            return true;
        }

        private bool AllAvailable(IEnumerable<string> side, out string busyName)
        {
            busyName = null;
            foreach (var uid in side)
            {
                if (!(sapi.World.PlayerByUid(uid) is IServerPlayer player))
                {
                    busyName = NameOf(uid);
                    return false;
                }
                if (manager.IsBusy(uid))
                {
                    busyName = player.PlayerName;
                    return false;
                }
            }
            return true;
        }

        // Bookkeeping

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

        /// <summary>Drops anything this player is part of. Called on disconnect: a challenge whose
        /// side is gone can never be played.</summary>
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

        /// <summary>The challenge this player can answer, or null.</summary>
        private DuelChallenge PendingFor(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return null;
            return pending.Values.FirstOrDefault(c => c.ChallengerUid == uid || c.TargetUid == uid);
        }

        // Talking to clients

        private void PromptTarget(DuelChallenge challenge, IServerPlayer target, IServerPlayer challenger)
        {
            manager.NetworkHandler?.SendDuelPrompt(target, challenge, challenger.PlayerName);

            // Chat as well as the popup: the popup can be dismissed, and the command still works
            // until the deadline.
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

        private string NameOf(string uid)
            => sapi.World.PlayerByUid(uid)?.PlayerName ?? uid;
    }
}
