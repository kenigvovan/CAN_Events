using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers.eventBehavior.Shared;
using canevents.src.EventManagers.events;
using canevents.src.QueueManagers;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace canevents.src.EventManagers
{
    // Open matches: no queue, one live match per event code, players walk in and out.
    public partial class CANEventManager
    {
        // Admin-stopped this session; the keeper leaves them down until /eventadmin live start.
        private readonly HashSet<string> stoppedOpenEvents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // code -> when the keeper last complained, so a broken entry doesn't fill the log every 15 s.
        private readonly Dictionary<string, long> keeperWarnedAtMs = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private const long KeeperWarnEveryMs = 10 * 60 * 1000;

        private long openMatchKeeperId = -1;

        // A minute after boot so places and chunks are up; then every 15 s brings back what died.
        public void StartOpenMatchKeeper()
        {
            if (openMatchKeeperId >= 0) return;
            openMatchKeeperId = sapi.Event.RegisterGameTickListener(_ => KeepOpenMatchesUp(), 15000, 60000);
        }

        private void KeepOpenMatchesUp()
        {
            foreach (var code in config.openEvents ?? new string[0])
            {
                if (stoppedOpenEvents.Contains(code)) continue;
                var ev = FindEventByCode(code);
                if (ev != null && FindOpenMatch(code) != null) continue;

                var (ok, message) = ev == null
                    ? (false, Lang.Get("canevents:event-not-found", code))
                    : StartOpenMatch(ev);
                if (ok) { keeperWarnedAtMs.Remove(code); continue; }

                long now = sapi.World.ElapsedMilliseconds;
                if (keeperWarnedAtMs.TryGetValue(code, out long last) && now - last < KeeperWarnEveryMs) continue;
                keeperWarnedAtMs[code] = now;
                log.Warning("open match {0} not started: {1}", code, message);
            }
        }

        public static IOpenMatchMode OpenModeOf(CANEvent ev) => ev?.GetBehavior<IOpenMatchMode>();

        // Teams a match of this event is played with: the open mode's own count, else the queue's sides.
        public static int TeamCountOf(CANEvent ev)
            => Math.Max(2, OpenModeOf(ev)?.Teams ?? ev?.AssemblingEB?.MaxPartiesCount ?? 2);

        public RunningEvent FindOpenMatch(string code)
            => RunningEventsSnapshot().FirstOrDefault(re =>
                re.IsOpenMatch && re.EventState != EventState.Finished && re.Event.CodeEquals(code));

        public (bool ok, string message) StartOpenMatch(CANEvent ev)
        {
            var mode = OpenModeOf(ev);
            if (mode == null) return (false, Lang.Get("canevents:live-not-open-event", ev?.Code ?? "?"));
            if (FindOpenMatch(ev.Code) != null) return (false, Lang.Get("canevents:live-already-running", ev.Code));

            // Empty teams; the roster fills as people walk in.
            var assembly = new TeamAssembly();
            for (int i = 0; i < mode.Teams; i++)
                assembly.AddShard(new TeamShard(new Registration(ev, new List<string>())));

            var re = StartMatch(ev, assembly, MatchStartSource.External, allowBots: false, configure: m =>
            {
                m.IsOpenMatch = true;
                m.Unlimited = true;
                m.PerTeamSpawns = true;
                m.SuppressRating = true;
                m.SuppressRewards = true;
                m.SuppressEntryFeeSettlement = true;
            });

            stoppedOpenEvents.Remove(ev.Code);
            sapi.World.Logger.Notification("Open match {0} created (id {1}, {2} teams)", ev.Code, re.Id, mode.Teams);
            NetworkHandler?.BroadcastEventList();
            return (true, Lang.Get("canevents:live-started", ev.Code));
        }

        public (bool ok, string message) StopOpenMatch(string code)
        {
            var re = FindOpenMatch(code);
            if (re == null) return (false, Lang.Get("canevents:live-not-running", code));
            StopOpenMatch(re);
            return (true, Lang.Get("canevents:live-stopped", code));
        }

        // Also from /eventadmin stop: an admin stop holds until /eventadmin live start.
        internal void StopOpenMatch(RunningEvent re)
        {
            stoppedOpenEvents.Add(re.Event.Code);
            // Searching for an arena: never started, nothing to hand back.
            if (re.EventState == EventState.SearchingForLocation)
                AbortSearchingEvent(re, "stopped by an admin");
            else
                FinishEvent(re);
        }

        // Lobby entry: pick a class first unless there is nothing to pick, then admit.
        public (bool ok, string message) JoinOpenMatch(IServerPlayer player, string code, int team)
        {
            var re = FindOpenMatch(code);
            var refusal = OpenEntryRefusal(re, player, team, code);
            if (refusal != null) return (false, refusal);

            const float pickSeconds = 60f;
            bool asked = NetworkHandler?.RequestLoadoutPickFor(re, player, pickSeconds,
                "canevents:gui-pick-closes-in", pl =>
                {
                    // The world moved on while the window was open.
                    var (ok, message) = AdmitToMatch(re, pl, team);
                    if (!ok) pl.SendMessage(0, message, EnumChatType.Notification);
                }) ?? false;

            return asked ? (true, Lang.Get("canevents:live-pick-class")) : AdmitToMatch(re, player, team);
        }

        private string OpenEntryRefusal(RunningEvent re, IServerPlayer player, int team, string code)
        {
            if (re == null || re.EventState != EventState.Running) return Lang.Get("canevents:live-not-running", code);
            if (team < 0 || team >= re.ParticipantsByGroupsBefore.Count)
                return Lang.Get("canevents:live-no-such-team", re.ParticipantsByGroupsBefore.Count);
            if (IsBusy(player.PlayerUID)) return Lang.Get("canevents:live-busy");
            if (player.Entity == null || !player.Entity.Alive) return Lang.Get("canevents:live-dead");
            if (_store.SnapshotForPlayer(player.PlayerUID).Count > 0) return Lang.Get("canevents:live-queued");

            // Not a way out of a fight elsewhere: the arena would stash their inventory and teleport them off.
            if (Compat.DownedCompat.IsDowned(player.Entity)) return Lang.Get("canevents:live-downed");
            if (player.WorldData?.CurrentGameMode != EnumGameMode.Survival) return Lang.Get("canevents:live-not-survival");
            int tag = CombatTag.SecondsLeft(sapi, player.PlayerUID, config.combatTagSeconds);
            if (tag > 0) return Lang.Get("canevents:live-in-combat", tag);

            int max = OpenModeOf(re.Event)?.MaxImbalance ?? 0;
            if (max > 0)
            {
                int smallest = Enumerable.Range(0, re.ParticipantsByGroupsBefore.Count).Min(re.CountInTeam);
                if (re.CountInTeam(team) - smallest >= max)
                    return Lang.Get("canevents:live-team-full", TeamPalette.NameOf(team));
            }
            return null;
        }

        public (bool ok, string message) AdmitToMatch(RunningEvent re, IServerPlayer player, int team)
        {
            var refusal = OpenEntryRefusal(re, player, team, re?.Event?.Code);
            if (refusal != null) return (false, refusal);

            foreach (var beh in re.Behaviors)
                if (!beh.CanAdmit(player)) return (false, Lang.Get("canevents:live-busy"));

            // Snapshot first; any later refusal hands it straight back.
            var save = re.GetBehavior<eventBehavior.SavePlayerAttributes>();
            if (save != null && !save.PrepareAdmit(player)) return (false, Lang.Get("canevents:inventory-unsettled-sitout"));

            string uid = player.PlayerUID;
            if (!re.Admit(uid, team))
            {
                save?.RollbackAdmit(player);
                return (false, Lang.Get("canevents:live-busy"));
            }

            Compat.DownedCompat.Clear(player);
            DispatchToBehaviors(re, "OnParticipantAdmitted", beh => beh.OnParticipantAdmitted(player));

            NetworkHandler?.SendStateUpdateTo(re, player);
            NetworkHandler?.SendScoreboardTo(re, player);
            BroadcastToParticipants(re, Lang.Get("canevents:live-joined", player.PlayerName, TeamPalette.NameOf(team)));
            sapi.World.Logger.Notification("{0} joined open match {1} on team {2}", player.PlayerName, re.Event.Code, team);
            return (true, null);
        }

        // /canlive leave and exit pads. Open match: back home with their things. Battle (or any match with
        // a waiting room): same, and their team plays on without them.
        public (bool ok, string message) LeaveOpenMatch(IServerPlayer player)
        {
            var re = FindLiveEventOf(player.PlayerUID);
            if (re == null || re.EventState == EventState.Finished) return (false, Lang.Get("canevents:live-not-in"));

            int tag = CombatTag.SecondsLeft(sapi, player.PlayerUID, config.combatTagSeconds);
            if (tag > 0 && re.IsActive(player.PlayerUID)) return (false, Lang.Get("canevents:live-in-combat", tag));

            if (re.IsOpenMatch)
            {
                RemoveFromOpenMatch(re, player, online: true);
                return (true, Lang.Get("canevents:live-left"));
            }
            if (re.GetBehavior<IPreStartLobby>() != null) return LeaveLobbyMatch(re, player);
            return (false, Lang.Get("canevents:live-not-in"));
        }

        // A battle-style match: out of play like a quitter, then home with their own inventory.
        private (bool ok, string message) LeaveLobbyMatch(RunningEvent re, IServerPlayer player)
        {
            string uid = player.PlayerUID;
            NetworkHandler?.CancelLoadoutPickFor(re.Id, uid);
            if (re.IsActive(uid))
            {
                re.MarkLeftInPlay(uid);
                re.RemoveFromPlay(uid);
            }
            // Pre-roster nobody is active and nothing was taken yet.
            if (re.EventState == EventState.AwaitingReady || re.EventState == EventState.SearchingForLocation)
                re.MarkPreStartDropout(uid);
            else
                TryReleaseEliminated(player);

            BroadcastToParticipants(re, Lang.Get("canevents:live-player-left", player.PlayerName));
            return (true, Lang.Get("canevents:live-left"));
        }

        // Off the roster first, so the damage and death sweeps stop treating them as ours.
        // online=false (disconnect): no inventory or teleport work on a leaving player; reconnect settles both.
        private void RemoveFromOpenMatch(RunningEvent re, IServerPlayer player, bool online)
        {
            string uid = player.PlayerUID;
            NetworkHandler?.CancelLoadoutPickFor(re.Id, uid);
            re.Withdraw(uid);

            if (online)
            {
                SpectatorManager?.RemoveSpectator(player);
                if (player.Entity != null && !player.Entity.Alive) player.Entity.Revive();
                Compat.DownedCompat.Clear(player);
                EventBroadcaster.ClearSpeedMutator(player);
                DispatchToBehaviors(re, "OnParticipantWithdrawn", beh => beh.OnParticipantWithdrawn(player));
                NetworkHandler?.SendMatchClosedTo(re, player);
            }
            else
            {
                DispatchToBehaviors(re, "OnParticipantDropped", beh => beh.OnParticipantDropped(uid));
            }
            BroadcastToParticipants(re, Lang.Get("canevents:live-player-left", player.PlayerName));
        }

        // Debug: bots walk into a running open match, each onto the smallest team, and respawn at its base.
        // Needs an ArenaBots behavior on the event (it spawns and respawns the bodies).
        public (bool ok, string message) AddBotsToOpenMatch(string code, int count)
        {
            var re = FindOpenMatch(code);
            if (re == null || re.EventState != EventState.Running) return (false, Lang.Get("canevents:live-not-running", code));
            var bots = re.GetBehavior<eventBehavior.ArenaBotsEB>();
            if (bots == null) return (false, Lang.Get("canevents:live-no-arenabots", re.Event.Code));

            var scorer = re.GetBehavior<eventBehavior.abstracts.KillScoringEB>();
            int teams = re.ParticipantsByGroupsBefore.Count;
            int existing = re.ParticipantsBefore.Count(Bots.BotIdentity.IsBot);
            int added = 0;
            for (int i = 0; i < Math.Clamp(count, 1, 16); i++)
            {
                string uid = Bots.BotIdentity.NewUid(existing + i);
                int team = Enumerable.Range(0, teams).OrderBy(re.CountInTeam).First();
                if (!re.Admit(uid, team)) continue;
                scorer?.SeedCounts(uid);
                if (!bots.SpawnBody(uid))
                {
                    re.Withdraw(uid);
                    continue;
                }
                added++;
            }
            NetworkHandler?.SendStateUpdate(re);
            return (added > 0, Lang.Get("canevents:live-bots-added", added, re.Event.Code));
        }

        public (bool ok, string message) RemoveBotsFromOpenMatch(string code)
        {
            var re = FindOpenMatch(code);
            if (re == null) return (false, Lang.Get("canevents:live-not-running", code));
            var bots = re.GetBehavior<eventBehavior.ArenaBotsEB>();

            var uids = re.ParticipantsBefore.Where(Bots.BotIdentity.IsBot).ToList();
            foreach (var uid in uids)
            {
                re.Withdraw(uid);
                bots?.Despawn(uid);
            }
            NetworkHandler?.SendStateUpdate(re);
            return (true, Lang.Get("canevents:live-bots-removed", uids.Count, re.Event.Code));
        }

        // Side pad in a waiting room: the lobby of whatever match is held in that place's room
        // (a zone without a place: the lobby of its own event). Outside a lobby countdown it does nothing.
        public (bool ok, string message) PickSideInLobby(IServerPlayer player, string code, int team, long placeId = 0)
        {
            var re = RunningEventsSnapshot().FirstOrDefault(m =>
                m.EventState == EventState.LocationFound && m.IsActive(player.PlayerUID)
                && (placeId != 0 ? m.Place?.Id == placeId : m.Event.CodeEquals(code)));
            var room = re?.GetBehavior<eventBehavior.WaitingRoomEB>();
            return room == null ? (false, null) : room.TryPick(player, team);
        }
    }
}
