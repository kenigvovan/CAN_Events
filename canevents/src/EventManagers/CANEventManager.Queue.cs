using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers.events;
using canevents.src.QueueManagers;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace canevents.src.EventManagers
{
    public partial class CANEventManager
    {
        bool AddRegistration(Registration registration)
        {
            return _store.Add(registration);
        }

        bool RemoveRegistration(Registration registration)
        {
            bool removed = _store.Remove(registration);
            // QM must drop it from partial assemblies.
            if (QueueManagers.TryGetValue(registration.Event.Code, out var qm))
                qm.OnRegistrationRemoved(registration);
            return removed;
        }

        // Don't mutate the returned lists.
        public Dictionary<CANEvent, List<Registration>> SnapshotRegistrations()
            => _store.Snapshot();

        public int CountRegistrations(CANEvent canEvent)
            => _store.CountFor(canEvent);

        public Registration FindRegistration(string uid, CANEvent ev)
            => _store.FindRegistration(uid, ev);

        // Running matches keep going; pending registrations are dropped.
        public void SetEventEnabled(string code, bool enabled)
        {
            if (string.IsNullOrEmpty(code)) return;
            AdminState.SetEventEnabled(code, enabled);
            if (!enabled) DropQueueRegistrationsFor(code);
        }

        public bool SetPlayerBlacklisted(string uid, bool blacklisted)
        {
            if (string.IsNullOrEmpty(uid)) return false;
            bool changed = AdminState.SetPlayerBlacklisted(uid, blacklisted);
            if (blacklisted) DropQueueRegistrationsForPlayer(uid);
            return changed;
        }

        private void DropStalePartyRegistrations()
            => DropRegistrations(reg =>
            {
                if (reg.PlayersGuids == null || reg.PlayersGuids.Count <= 1) return false;
                var anchor = sapi.World.PlayerByUid(reg.PlayersGuids[0]) as IServerPlayer;
                return !(anchor != null
                    && PartyManager.GetPlayerParty(anchor, out var party)
                    && party.playersGuids.SetEquals(reg.PlayersGuids));
            },
            _ => Lang.Get("canevents:queue-party-stale"));

        // Also notifies the QM, refunds escrow and refreshes the browser. messageFor may return null.
        // System.* spelled out: Vintagestory.API.Common has its own Func<>.
        private bool DropRegistrations(System.Predicate<Registration> predicate,
            System.Func<string, string> messageFor)
        {
            var dropped = _store.DropWhere(predicate);
            if (dropped.Count == 0) return false;

            foreach (var (reg, affected) in dropped)
            {
                if (QueueManagers.TryGetValue(reg.Event.Code, out var qm))
                    qm.OnRegistrationRemoved(reg);

                foreach (var uid in affected)
                {
                    RefundIfNoLongerCommitted(uid);  // offline players are refunded on reconnect instead
                    string message = messageFor(uid);
                    if (message == null) continue;
                    (sapi.World.PlayerByUid(uid) as IServerPlayer)?.SendMessage(0, message, EnumChatType.Notification);
                }
            }

            NetworkHandler?.BroadcastEventList();
            return true;
        }

        // Party registrations are dropped whole.
        private void DropQueueRegistrationsForPlayer(string playerUid)
            => DropRegistrations(reg => reg.PlayersGuids.Contains(playerUid),
                uid => uid == playerUid ? null : Lang.Get("canevents:queue-member-disconnected"));

        // Same drop, different message for the party.
        internal bool DropQueueRegistrationsToSpectate(string playerUid, string playerName)
            => DropRegistrations(reg => reg.PlayersGuids.Contains(playerUid),
                uid => uid == playerUid ? null : Lang.Get("canevents:queue-member-spectating", playerName));

        private void DropQueueRegistrationsFor(string code)
            => DropRegistrations(reg => reg.Event.CodeEquals(code),
                _ => Lang.Get("canevents:queue-disabled-dropped", code));

        public (bool ok, string message) AddToQueue(CANEvent canEvent, IServerPlayer player, string loadoutSet = null)
        {
            if (!IsEventEnabled(canEvent.Code))
                return (false, Lang.Get("canevents:queue-disabled", canEvent.Code));

            if (IsPlayerBlacklisted(player.PlayerUID))
                return (false, Lang.Get("canevents:queue-blacklisted"));

            if (Tournaments != null && Tournaments.IsLockedEntrant(player.PlayerUID))
                return (false, Lang.Get("canevents:queue-tournament-locked"));

            Registration registration;

            if (PartyManager.GetPlayerParty(player, out var party))
            {
                if (party.leaderGuid != player.PlayerUID)
                    return (false, Lang.Get("canevents:queue-party-leader-only"));

                foreach (var member in party.playersGuids)
                {
                    if (IsPlayerBlacklisted(member))
                        return (false, Lang.Get("canevents:queue-blacklisted-party"));
                    if (IsOccupiedByAMatch(member))
                        return (false, Lang.Get("canevents:queue-in-match-party"));
                    if (Tournaments != null && Tournaments.IsLockedEntrant(member))
                        return (false, Lang.Get("canevents:queue-tournament-locked-party"));
                    if (_store.HasPlayerRegistration(member, canEvent))
                        return (false, Lang.Get("canevents:queue-party-already"));
                }
                registration = new Registration(canEvent, party.playersGuids.ToList());
            }
            else
            {
                // R24b: a queued player in a match would be skipped by assembly and start a second match after.
                if (IsOccupiedByAMatch(player.PlayerUID))
                    return (false, Lang.Get("canevents:queue-in-match"));
                if (_store.HasPlayerRegistration(player.PlayerUID, canEvent))
                    return (false, Lang.Get("canevents:queue-already"));
                registration = new Registration(canEvent, new List<string> { player.PlayerUID });
            }

            registration.ChosenLoadoutSet = loadoutSet;
            registration.RegistrantUid    = player.PlayerUID;

            if (QueueManagers.TryGetValue(canEvent.Code, out var qm))
            {
                var check = qm.CheckRegistrationIsSuitable(registration);
                if (!check.ok)
                    return (false, check.message ?? Lang.Get("canevents:queue-unsuitable"));
            }

            // Validate every member before charging anyone; escrow has a single slot.
            var fee = canEvent.EntryFee?.Items;
            var required = ItemSetUtil.Coalesce(fee);
            if (required.Count > 0)
            {
                foreach (var uid in registration.PlayersGuids)
                {
                    var member = sapi.World.PlayerByUid(uid) as IServerPlayer;
                    if (member == null || !InventoryUtil.Has(member, required))
                        return (false, Lang.Get("canevents:queue-entryfee-missing"));
                    if (EntryFeeEscrow.Has(member))
                        return (false, Lang.Get("canevents:queue-entryfee-pending"));
                }
            }

            if (!AddRegistration(registration))
                return (false, Lang.Get("canevents:queue-add-failed"));

            // Charged now so the fee can't be dropped before the start.
            if (required.Count > 0)
            {
                foreach (var uid in registration.PlayersGuids)
                {
                    if (sapi.World.PlayerByUid(uid) is IServerPlayer member
                        && EntryFeeEscrow.Charge(member, canEvent.Code, fee))
                        member.SendMessage(0, Lang.Get("canevents:entryfee-charged"), EnumChatType.Notification);
                }
            }

            return (true, Lang.Get("canevents:registered", canEvent.Code));
        }

        public (bool ok, string reason) RemoveFromQueue(CANEvent canEvent, IServerPlayer player)
        {
            var registration = _store.FindRegistration(player.PlayerUID, canEvent);
            if (registration == null) return (false, null);

            if (PartyManager.GetPlayerParty(player, out var party)
                && party.leaderGuid != player.PlayerUID
                && registration.PlayersGuids.Count > 1)
            {
                var leader     = sapi.World.PlayerByUid(party.leaderGuid);
                string leaderName = leader?.PlayerName ?? party.leaderGuid;
                return (false, Lang.Get("canevents:queue-party-leader-leave-only", leaderName));
            }

            bool removed = RemoveRegistration(registration);
            if (removed)
                foreach (var uid in registration.PlayersGuids)
                    RefundIfNoLongerCommitted(uid);
            return (removed, null);
        }

        // configure runs before the match is listed or announced, so observers see its final flags.
        private RunningEvent StartMatch(CANEvent canEvent, TeamAssembly assembly,
            MatchStartSource source, bool allowBots = true, System.Action<RunningEvent> configure = null)
        {
            // Bots are added here, not in the QMs: they never queue or pay. Tournaments pass allowBots=false.
            int bots = allowBots ? Bots.BotFiller.Fill(canEvent, assembly, sapi.World.Logger) : 0;
            if (bots > 0)
                sapi.World.Logger.Notification("[bots] Filled event {0} with {1} bot(s)", canEvent.Code, bots);

            var re = new RunningEvent(sapi, canEvent, assembly, MatchServices);
            configure?.Invoke(re);
            AddRunningEvent(re);

            if (source == MatchStartSource.Queue)
            {
                var regs = assembly.Shards.SelectMany(s => s.Registrations).Distinct().ToList();
                lock (runningEvents) queueRegistrationsByMatch[re.Id] = regs;
            }

            // Queue only; tournaments run their own ready check.
            bool awaitingReady = false;
            if (source == MatchStartSource.Queue && config.queueReadyCheckSeconds > 0)
                log.Safely("BeginReadyCheck for " + canEvent.Code, () =>
                {
                    BeginReadyCheck(re);
                    awaitingReady = re.EventState == EventState.AwaitingReady;
                });

            // Ready-check matches are announced by CloseReadyCheck.
            if (!awaitingReady) RaiseMatchStarted(re, source);
            return re;
        }

        public void AttemptToComposeTeams()
        {
            int runningBefore = RunningEventCount;
            var snapshot      = _store.Snapshot();
            int regsBefore    = snapshot.Values.Sum(l => l.Count);

            foreach (var (canEvent, registrations) in snapshot)
            {
                // Signup window holds the queue open.
                if (QueueHold != null && QueueHold(canEvent.Code)) continue;
                AssembleFrom(canEvent, registrations);
            }

            int regsAfter = _store.Snapshot().Values.Sum(l => l.Count);
            if (RunningEventCount != runningBefore || regsAfter != regsBefore)
                NetworkHandler?.BroadcastEventList();
        }

        // Ignores QueueHold (signup window deadline) but keeps the mode's minimums, unlike ForceComposeEvent.
        public int AssembleQueuedNow(CANEvent canEvent)
        {
            if (canEvent == null) return 0;
            if (!_store.Snapshot().TryGetValue(canEvent, out var registrations)) return 0;

            int started = AssembleFrom(canEvent, registrations);
            if (started > 0) NetworkHandler?.BroadcastEventList();
            return started;
        }

        private int AssembleFrom(CANEvent canEvent, List<Registration> registrations)
        {
            if (registrations == null || registrations.Count == 0) return 0;
            if (!QueueManagers.TryGetValue(canEvent.Code, out var qm)) return 0;

            int started = 0;
            var assemblies = qm.TryAssemble(canEvent, registrations);
            foreach (var assembly in assemblies)
            {
                var consumed = assembly.Shards.SelectMany(s => s.Registrations).Distinct().ToList();
                foreach (var reg in consumed)
                    RemoveRegistration(reg);

                var re = StartMatch(canEvent, assembly, MatchStartSource.Queue);
                started++;

                // One match at a time: a registration elsewhere would hold a slot the player can't take
                // (assembly skips busy players) and start another match once this one ends (R24a).
                var inMatch = new HashSet<string>(consumed.SelectMany(r => r.PlayersGuids));
                DropRegistrations(reg => reg.PlayersGuids.Any(inMatch.Contains),
                    _ => Lang.Get("canevents:queue-dropped-match-started", canEvent.Code));
                sapi.World.Logger.Notification(
                    "Team assembly completed for event {0}, RunningEvent {1} created", canEvent.Code, re.Id);
            }
            return started;
        }

        // /eventadmin forcestart: each registration is its own team, QM thresholds ignored.
        public bool ForceComposeEvent(CANEvent canEvent, out string reason)
        {
            if (OpenModeOf(canEvent) != null)
            {
                reason = "it is an open match - use /eventadmin live start";
                return false;
            }
            if (!QueueManagers.ContainsKey(canEvent.Code))
            {
                reason = "no queue manager wired for this event";
                return false;
            }

            var snapshot = _store.Snapshot();
            if (!snapshot.TryGetValue(canEvent, out var registrations) || registrations.Count == 0)
            {
                reason = "nobody is queued for it";
                return false;
            }

            // MarkedUsed is ignored on purpose; busy players are not (would clobber their inventory backup).
            var usable = registrations.Where(r => !r.PlayersGuids.Any(IsPlayerBusy)).ToList();
            if (usable.Count == 0)
            {
                reason = "everyone queued for it is already in another match";
                return false;
            }

            var eb = canEvent.AssemblingEB;
            int minSides = Math.Max(1, eb?.MinPartiesCount ?? 1);
            int botSides = Bots.BotFiller.SidesItCanFill(canEvent);
            if (Math.Max(usable.Count, botSides) < minSides)
            {
                reason = $"only {usable.Count} of the {minSides} sides it needs are queued";
                return false;
            }

            var assembly = new TeamAssembly();
            foreach (var reg in usable)
                assembly.AddShard(new TeamShard(reg));

            foreach (var reg in usable)
                RemoveRegistration(reg);

            var re = StartMatch(canEvent, assembly, MatchStartSource.ForceStart);
            sapi.World.Logger.Notification(
                "Force-start: event {0} composed from {1} registration(s), RunningEvent {2} created",
                canEvent.Code, usable.Count, re.Id);

            NetworkHandler?.BroadcastEventList();
            reason = null;
            return true;
        }

        // Tournament entry point: exact teams, no QM, no bots. Registrations never hit the store,
        // so party sweeps and RefundIfNoLongerCommitted don't see them.
        public RunningEvent StartMatchFor(
            CANEvent canEvent,
            IReadOnlyList<IReadOnlyList<string>> teams,
            IReadOnlyDictionary<string, string> loadoutChoices,
            IReadOnlyCollection<long> allowedPlaceIds,
            out string reason)
        {
            reason = null;

            if (canEvent == null) { reason = "no such event"; return null; }
            if (teams == null || teams.Count < 2) { reason = "need at least two teams"; return null; }

            foreach (var team in teams)
            {
                if (team == null || team.Count == 0) { reason = "an empty team was passed"; return null; }
                foreach (var uid in team)
                {
                    // The caller is the tournament itself.
                    switch (PlayerState.Check(uid, ignoreTournamentCall: true))
                    {
                        case PlayerAvailability.Offline:
                            reason = NameOf(uid) + " is offline";
                            return null;
                        case PlayerAvailability.Busy:
                            reason = NameOf(uid) + " is already in a match";
                            return null;
                    }
                }
            }

            var assembly = new TeamAssembly();
            foreach (var team in teams)
            {
                var reg = new Registration(canEvent, team.ToList()) { RegistrantUid = team[0] };
                if (loadoutChoices != null)
                {
                    foreach (var uid in team)
                        if (loadoutChoices.TryGetValue(uid, out var set) && !string.IsNullOrEmpty(set))
                            reg.PerPlayerLoadoutChoices[uid] = set;
                }
                assembly.AddShard(new TeamShard(reg));
            }

            var re = StartMatch(canEvent, assembly, MatchStartSource.External, allowBots: false);
            if (allowedPlaceIds != null && allowedPlaceIds.Count > 0)
                re.AllowedPlaceIds = new HashSet<long>(allowedPlaceIds);

            sapi.World.Logger.Notification(
                "Direct start: event {0} with {1} team(s), RunningEvent {2} created", canEvent.Code, teams.Count, re.Id);

            NetworkHandler?.BroadcastEventList();
            return re;
        }
    }
}
