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
    // Queue/registration management: add/remove from queue, stale-party cleanup,
    // event enable/disable toggle, team-assembly trigger.
    public partial class CANEventManager
    {
        // Internal add/remove

        bool AddRegistration(Registration registration)
        {
            return _store.Add(registration);
        }

        bool RemoveRegistration(Registration registration)
        {
            bool removed = _store.Remove(registration);
            // Notify the QM so partial assemblies that contained this reg are cleaned up.
            if (QueueManagers.TryGetValue(registration.Event.Code, out var qm))
                qm.OnRegistrationRemoved(registration);
            return removed;
        }

        /// <summary>Per-event snapshot of current registrations. Safe to iterate without locking;
        /// callers must NOT mutate the returned lists.</summary>
        public Dictionary<CANEvent, List<Registration>> SnapshotRegistrations()
            => _store.Snapshot();

        /// <summary>How many sides are queued for one event, without copying the whole store.</summary>
        public int CountRegistrations(CANEvent canEvent)
            => _store.CountFor(canEvent);

        /// <summary>The pending registration for this player in this event, or null. Public so
        /// NetworkHandler need not touch _store directly.</summary>
        public Registration FindRegistration(string uid, CANEvent ev)
            => _store.FindRegistration(uid, ev);

        // Enable / disable

        /// <summary>
        /// Toggles whether new registrations and auto-scheduling are allowed for an event code.
        /// Running matches are NOT stopped; pending registrations for a disabled event are dropped.
        /// </summary>
        public void SetEventEnabled(string code, bool enabled)
        {
            if (string.IsNullOrEmpty(code)) return;
            AdminState.SetEventEnabled(code, enabled);   // persists itself when the set changed
            if (!enabled) DropQueueRegistrationsFor(code);
        }

        /// <summary>
        /// Adds/removes a player from the queue blacklist and persists. Blacklisting also drops their
        /// pending registrations so an in-progress signup can't slip through. True if the set changed.
        /// </summary>
        public bool SetPlayerBlacklisted(string uid, bool blacklisted)
        {
            if (string.IsNullOrEmpty(uid)) return false;
            bool changed = AdminState.SetPlayerBlacklisted(uid, blacklisted);
            if (blacklisted) DropQueueRegistrationsForPlayer(uid);
            return changed;
        }

        // Stale-party / disconnect / disable drops

        /// <summary>Removes every multi-player registration whose snapshot no longer matches an
        /// existing party with the same membership, notifying those players.</summary>
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

        /// <summary>
        /// Pulls every matching registration out of the queue along with the three things that must
        /// always go with it: telling the queue manager (a forgotten OnRegistrationRemoved leaves a
        /// ghost in a partial assembly), refunding each affected player's escrow, and refreshing the
        /// browser. <paramref name="messageFor"/> may return null to tell a given player nothing.
        /// </summary>
        // Namespaces spelled out: Vintagestory.API.Common declares a Func<> of its own and both
        // namespaces are in scope.
        private void DropRegistrations(System.Predicate<Registration> predicate,
            System.Func<string, string> messageFor)
        {
            var dropped = _store.DropWhere(predicate);
            if (dropped.Count == 0) return;

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
        }

        /// <summary>Drops every pending registration involving the player (on disconnect, so an offline
        /// player can't be pulled into a forming match). Party registrations are dropped whole.</summary>
        private void DropQueueRegistrationsForPlayer(string playerUid)
            => DropRegistrations(reg => reg.PlayersGuids.Contains(playerUid),
                // The player who left needs no message; the party members left behind do.
                uid => uid == playerUid ? null : Lang.Get("canevents:queue-member-disconnected"));

        private void DropQueueRegistrationsFor(string code)
            => DropRegistrations(reg => reg.Event.CodeEquals(code),
                _ => Lang.Get("canevents:queue-disabled-dropped", code));

        // Public queue API

        public (bool ok, string message) AddToQueue(CANEvent canEvent, IServerPlayer player, string loadoutSet = null)
        {
            if (!IsEventEnabled(canEvent.Code))
                return (false, Lang.Get("canevents:queue-disabled", canEvent.Code));

            if (IsPlayerBlacklisted(player.PlayerUID))
                return (false, Lang.Get("canevents:queue-blacklisted"));

            // A bracket that locks its entrants owns their time until it is over, or they sit in casual
            // queues between rounds and stall the whole tournament.
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
                    if (Tournaments != null && Tournaments.IsLockedEntrant(member))
                        return (false, Lang.Get("canevents:queue-tournament-locked-party"));
                    if (_store.HasPlayerRegistration(member, canEvent))
                        return (false, Lang.Get("canevents:queue-party-already"));
                }
                registration = new Registration(canEvent, party.playersGuids.ToList());
            }
            else
            {
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

            // Every member must be online, hold no other pending wager (a single escrow slot) and carry
            // the full fee. Validated up front so the whole registration is rejected before any charge.
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

            // Charge at commit so the fee can't be dropped before the match starts; refunded if they
            // leave before assembly.
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
                    RefundIfNoLongerCommitted(uid);  // give back the entry-fee escrow if they paid one
            return (removed, null);
        }

        // Team assembly

        /// <summary>Turns a finished team assembly into a live match, with MatchServices attached from
        /// the first hook. Both the normal path and the admin force-start end here.</summary>
        private RunningEvent StartMatch(CANEvent canEvent, TeamAssembly assembly,
            MatchStartSource source, bool allowBots = true)
        {
            // Bots join here rather than in the queue managers: they never queue, hold a registration
            // or pay a fee, and this is the one place both start paths pass through. A tournament opts
            // out - its line-up is the bracket pair, and a bot can neither pay into nor take from the pot.
            int bots = allowBots ? Bots.BotFiller.Fill(canEvent, assembly) : 0;
            if (bots > 0)
                sapi.World.Logger.Notification("[bots] Filled event {0} with {1} bot(s)", canEvent.Code, bots);

            var re = new RunningEvent(sapi, canEvent, assembly, MatchServices);
            lock (CurrentlyRunningEvents)
                CurrentlyRunningEvents.Add(re);

            // The one place every start path passes through, so the one place observers are told.
            // Wrapped: a throwing observer must not take the match down with it.
            log.Safely("OnMatchStarted observer for " + canEvent.Code,
                () => OnMatchStarted?.Invoke(re, source));
            return re;
        }

        public void AttemptToComposeTeams()
        {
            int runningBefore = CurrentlyRunningEvents.Count;
            var snapshot      = _store.Snapshot();
            int regsBefore    = snapshot.Values.Sum(l => l.Count);

            foreach (var (canEvent, registrations) in snapshot)
            {
                if (registrations.Count == 0) continue;
                // Something may be holding this event's queue open on purpose (a signup window):
                // assembling now would start the match on the mode's minimum, which is exactly what
                // the hold exists to prevent.
                if (QueueHold != null && QueueHold(canEvent.Code)) continue;
                if (!QueueManagers.TryGetValue(canEvent.Code, out var qm)) continue;

                var assemblies = qm.TryAssemble(canEvent, registrations);
                foreach (var assembly in assemblies)
                {
                    var consumed = assembly.Shards.SelectMany(s => s.Registrations).Distinct().ToList();
                    foreach (var reg in consumed)
                        RemoveRegistration(reg);

                    var re = StartMatch(canEvent, assembly, MatchStartSource.Queue);
                    sapi.World.Logger.Notification(
                        "Team assembly completed for event {0}, RunningEvent {1} created", canEvent.Code, re.Id);
                }
            }

            int regsAfter = _store.Snapshot().Values.Sum(l => l.Count);
            if (CurrentlyRunningEvents.Count != runningBefore || regsAfter != regsBefore)
                NetworkHandler?.BroadcastEventList();
        }

        /// <summary>
        /// Starts an event now from whoever is already queued, bypassing the queue manager's
        /// thresholds (the admin override behind /eventadmin forcestart). Each registration becomes
        /// its own team. False when no usable registration is waiting.
        /// </summary>
        public bool ForceComposeEvent(CANEvent canEvent, out string reason)
        {
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

            // Every registration still in the store is fair game: MarkedUsed only means "sitting in some
            // (possibly partial) assembly", which is exactly what forcestart exists to override; regs of
            // started matches are already gone. Players busy in another live match are excluded, though:
            // double-booking would clobber their inventory backup and corrupt both rosters.
            var usable = registrations.Where(r => !r.PlayersGuids.Any(IsPlayerBusy)).ToList();
            if (usable.Count == 0)
            {
                reason = "everyone queued for it is already in another match";
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

        /// <summary>
        /// Starts an event now with exactly these teams (one inner list per team), bypassing the queue
        /// entirely: no queue manager, no bots, nobody else pulled in. The tournament driver's way in -
        /// unlike <see cref="ForceComposeEvent"/> the line-up is dictated and the live match is RETURNED
        /// so the caller can follow it to its result. The synthetic registrations deliberately never
        /// reach the store, so the party-change sweeps and RefundIfNoLongerCommitted cannot see them.
        /// </summary>
        /// <param name="loadoutChoices">Optional per-uid loadout set names; unlisted players are asked as usual.</param>
        /// <param name="allowedPlaceIds">Arena whitelist; null or empty means any suitable arena.</param>
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
                    // A bot side has no player behind it, so the online/busy checks do not apply.
                    if (Bots.BotIdentity.IsBot(uid)) continue;

                    if (!(sapi.World.PlayerByUid(uid) is IServerPlayer))
                    {
                        reason = NameOf(uid) + " is offline";
                        return null;
                    }
                    // Not IsPlayerBusy: that counts "called for a tournament match", which is precisely
                    // what these players are - the caller is the tournament.
                    if (IsOccupiedByAMatch(uid))
                    {
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
