using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace canevents.src.EventManagers
{
    // Death, respawn, disconnect, reconnect hooks.
    public partial class CANEventManager
    {
        public void OnDeath(IServerPlayer player, DamageSource source)
        {
            bool diedInAMatch = false;

            foreach (var re in RunningEventsSnapshot())
            {
                if (re.EventState != EventState.Running) continue;
                if (re.IsActive(player.PlayerUID))
                {
                    // Before elimination drops the return marker. Otherwise a corpse return teleporter leads into the arena.
                    diedInAMatch = true;
                    CorpseReturnGuard.MarkMatchDeath(player);

                    DispatchToBehaviors(re, "OnParticipantDeath",
                        beh => beh.OnParticipantDeath(player.PlayerUID, player.Entity, source));

                    if (!re.IsActive(player.PlayerUID))
                    {
                        SeatOutOfPlay(player, re);

                        if (re.Event.Rated && !re.SuppressRating && !Bots.BotIdentity.IsBot(player.PlayerUID))
                            Leaderboard?.RecordDeath(player.PlayerUID, re.Event.Code);
                    }
                }
            }

            // A normal world death supersedes the pending fix.
            if (!diedInAMatch) CorpseReturnGuard.ForgetPending(player);
        }

        // Bots and match mobs; players come through PlayerDeath.
        public void OnEntityDeath(Vintagestory.API.Common.Entities.Entity entity, DamageSource source)
        {
            if (entity is Vintagestory.API.Common.EntityPlayer) return;

            if (Mobs.MatchMobLink.IsMatchMob(entity))
            {
                Mobs.MatchMobManager.Of(Mobs.MatchMobLink.MatchIdOf(entity))?.OnMobDied(entity, source);
                return;
            }

            string uid = Bots.BotMatchLink.UidOf(entity);
            if (uid == null || !Bots.BotIdentity.IsBot(uid)) return;

            var re = Bots.BotMatchLink.MatchOf(entity);
            if (re == null || !re.IsParticipant(uid)) return;

            if (re.EventState == EventState.Running && re.IsActive(uid))
                DispatchToBehaviors(re, "OnParticipantDeath", beh => beh.OnParticipantDeath(uid, entity, source));

            var bots = re.GetBehavior<eventBehavior.ArenaBotsEB>();
            if (bots != null)
                log.Safely("ArenaBots.AfterBotDeath for " + re.Event?.Code, () => bots.AfterBotDeath(uid, entity, source));
            else if (re.IsActive(uid))
                re.EliminatePlayer(uid);
        }

        public void OnRespawn(IServerPlayer player)
        {
            // Vanilla has recorded the death spot by now.
            CorpseReturnGuard.ApplyPending(sapi, player);

            // PlayerRespawn reaches mods even when vanilla ignored the key because they're alive: a late
            // click after a round mode revived them would clear their gear. A real respawn is still dead
            // here, vanilla revives only once the spawn chunks are loaded (queued, never in this call).
            if (player.Entity == null || player.Entity.Alive) return;

            foreach (var re in RunningEventsSnapshot())
            {
                if (re.EventState != EventState.Running) continue;
                if (!re.HoldsPlayer(player.PlayerUID)) continue;

                if (!re.IsActive(player.PlayerUID))
                {
                    if (SpectatesOnElimination(re)) SpectatorManager?.ApplySpectatorState(player, re);
                    else
                    {
                        // continue: the respawn dispatch would clear the gear just returned.
                        TryReleaseEliminated(player);
                        continue;
                    }
                }

                // Vanilla hunger halves saturation and nutrients on revive, racing our 200ms dispatch. One-shot flag.
                player.Entity?.Attributes.SetBool("noSatietyRestoreOnRevive", true);

                // Engine clears inventory and teleports to spawn after PlayerRespawn fires, so defer.
                string uid = player.PlayerUID;
                var capturedRe = re;
                sapi.Event.RegisterCallback(_ =>
                {
                    var pl = sapi.World.PlayerByUid(uid) as IServerPlayer;
                    if (pl == null) return;
                    if (capturedRe.EventState != EventState.Running) return;
                    if (!capturedRe.HoldsPlayer(uid)) return;
                    DispatchToBehaviors(capturedRe, "OnParticipantRespawn", beh => beh.OnParticipantRespawn(pl));
                }, 200);
            }
        }

        public void OnDisconnect(IServerPlayer player)
        {
            // First, before teardown can throw: logs drops right after a teleport.
            EventTeleport.NoteDisconnect(sapi, player);

            NetworkHandler?.DropBracketViewer(player.PlayerUID);

            foreach (var re in RunningEventsSnapshot())
            {
                // Nothing to forfeit or decide; they are simply gone until they walk in again.
                if (re.IsOpenMatch)
                {
                    if (re.IsParticipant(player.PlayerUID)) RemoveFromOpenMatch(re, player, online: false);
                    continue;
                }

                if (re.IsActive(player.PlayerUID))
                {
                    re.MarkLeftInPlay(player.PlayerUID);
                    // Parkour is a race, leaving must not hand the win to the rest.
                    if (re.GetBehavior<eventBehavior.ParkourEB>() != null
                        || re.GetBehavior<eventBehavior.Shared.IDecidesOnDropout>() != null) re.RemoveFromPlay(player.PlayerUID);
                    else re.EliminatePlayer(player.PlayerUID);
                    BroadcastToParticipants(re, Lang.Get("canevents:player-disconnected-eliminated",
                        player.PlayerName, re.ParticipantsStillInEvent.Count));

                    DispatchToBehaviors(re, "OnParticipantLeftGame", beh => beh.OnParticipantLeftGame());
                }
                // Roster not built yet, EliminatePlayer would no-op.
                else if ((re.EventState == EventState.SearchingForLocation
                          || re.EventState == EventState.AwaitingReady)
                    && re.IsParticipant(player.PlayerUID))
                {
                    re.MarkPreStartDropout(player.PlayerUID);
                    re.ReadyUids.Remove(player.PlayerUID);
                }

                // Forfeit now while ModData is reachable; offline PlayerByUid is null. A crash fires no disconnect, so escrow survives.
                // Not for a dropout before the start: they never played, so their stake waits in escrow
                // and RefundIfNoLongerCommitted gives it back on their return (R27).
                if (re.IsParticipant(player.PlayerUID)
                    && !re.Roster.IsPreStartDropout(player.PlayerUID)
                    && re.Event.EntryFee != null
                    && re.Event.CodeEquals(EntryFeeEscrow.EventCodeOf(player)))
                {
                    var stake = EntryFeeEscrow.Read(player);
                    if (stake != null) RewardDistributor?.RecordForfeit(player.PlayerUID, re.Id, stake, re.Event?.Code);
                    EntryFeeEscrow.Clear(player);
                }

                // No RemoveSpectator: BroadcastPlayerData fails for a leaving player. RestoreOnReconnect handles it.
            }

            if (DropObserverFromMatches(player.PlayerUID))
                NetworkHandler?.ForgetSpectator(player.PlayerUID);

            SpectatorManager?.OnSpectatorDisconnect(player.PlayerUID);

            // Disconnect counts as Cancel for a template edit session.
            if (TemplateEditor != null && TemplateEditor.Sessions.ContainsKey(player.PlayerUID))
            {
                log.Safely("TemplateEditor.Cancel on disconnect for " + player.PlayerName,
                    () => TemplateEditor.Cancel(player));
            }

            DropQueueRegistrationsForPlayer(player.PlayerUID);

            sapi.ModLoader.GetModSystem<canevents>()?.Duels?.OnDisconnect(player.PlayerUID);
        }

        public void OnReconnect(IServerPlayer player)
        {
            // Logging in dead revives without a PlayerRespawn event.
            CorpseReturnGuard.ApplyPending(sapi, player);

            SpectatorManager?.RestoreOnReconnect(player);

            foreach (var re in RunningEventsSnapshot())
            {
                if (re.IsParticipant(player.PlayerUID))
                    DispatchToBehaviors(re, "OnParticipantJoinedGame", beh => beh.OnParticipantJoinedGame());
            }

            // Delay lets the engine's join-time positioning settle.
            bool released = TryReleaseEliminated(player, homeDelayMs: 300);

            // Keep the backup while still playing, or a later in-match death clears with nothing to restore.
            if (!released && !IsPlayingLiveEvent(player.PlayerUID)
                && !(SpectatorManager?.IsSpectating(player.PlayerUID) ?? false))
                RestoreEventBackup(player);

            RefundIfNoLongerCommitted(player.PlayerUID);

            RewardDistributor?.RefundForfeitIfStale(player,
                mid => FindRunningEvent(mid) != null);

            // Payouts that landed after they logged off.
            int paid = RewardDistributor?.Payouts?.Deliver(player, "reward") ?? 0;
            if (paid > 0)
                player.SendMessage(0, Lang.Get("canevents:payout-delivered"), EnumChatType.Notification);

            RestoreOrphanedReturnPosition(player);

            Tournaments?.OnReconnect(player);
        }

        // Crash recovery: return marker left with no live event means OnEnd never ran.
        private void RestoreOrphanedReturnPosition(IServerPlayer player)
        {
            if (IsInLiveEvent(player.PlayerUID)) return;
            if (!ReturnPosMarker.TryRead(player, sapi, out var home)) return;

            // Marker is cleared only when the teleport lands, so a disconnect in between retries.
            ReturnPosMarker.SendHome(sapi, player, home, "crash recovery", delayMs: 300);
            sapi.World.Logger.Notification("Returning {0} to pre-event position after orphaned event (crash recovery)", player.PlayerName);
        }

        private bool SpectatesOnElimination(RunningEvent re)
            => re?.GetBehavior<eventBehavior.SavePlayerAttributes>()?.SpectateOnElimination ?? true;

        // Also used for parkour finishers; they're only sent home once actually eliminated.
        public void SeatOutOfPlay(IServerPlayer player, RunningEvent re)
        {
            if (player == null || re == null) return;
            if (SpectatesOnElimination(re)) SpectatorManager?.MakeSpectator(player, re);
            else if (!re.IsActive(player.PlayerUID)) ReleaseOnElimination(player, re);
        }

        private void ReleaseOnElimination(IServerPlayer player, RunningEvent re)
        {
            string uid = player.PlayerUID;
            // Runs inside TriggerPlayerDeath; reviving immediately leaves them half-dead.
            sapi.Event.RegisterCallback(_ =>
            {
                if (re.EventState == EventState.Finished) return;

                var pl = sapi.World.PlayerByUid(uid) as IServerPlayer;
                if (pl?.Entity == null) return;
                if (!pl.Entity.Alive) pl.Entity.Revive();

                TryReleaseEliminated(pl);
            }, 100);
        }

        // Restore and send home an eliminated player early. Stays on the roster for rewards/results.
        public bool TryReleaseEliminated(IServerPlayer player, int homeDelayMs = 0)
        {
            if (player == null) return false;
            string uid = player.PlayerUID;

            var re = FindLiveEventOf(uid);
            if (re == null || re.IsActive(uid)) return false;
            // Pre-roster nobody is active yet.
            if (re.EventState == EventState.AwaitingReady || re.EventState == EventState.SearchingForLocation)
                return false;

            // Release first so death sweep/respawn clear can't touch the restored items.
            re.MarkReleased(uid);
            re.GetBehavior<eventBehavior.SavePlayerAttributes>()?.Release(uid);

            RestoreEventBackup(player);
            // Own-gear matches have no backup to restore, so RestoreEventBackup doesn't reset them.
            Compat.RpgClassesCompat.ResetCombat(player.Entity);
            // As when leaving an open match; the end-of-match clear would come only after they ran home with it.
            Compat.DownedCompat.Clear(player);
            eventBehavior.Shared.EventBroadcaster.ClearSpeedMutator(player);

            // Forget the match's return pos so the end teleport doesn't pull them back.
            re.GetBehavior<eventBehavior.TPThereAndBack>()?.ForgetReturnPos(uid);
            if (ReturnPosMarker.TryRead(player, sapi, out var home))
                ReturnPosMarker.SendHome(sapi, player, home, "released after elimination", homeDelayMs);

            SpectatorManager?.RemoveSpectator(player);
            return true;
        }

        private void RestoreEventBackup(IServerPlayer player)
        {
            if (!PlayerInventoryBackup.Restore(player, sapi)) return;
            // Only on a real restore: on a plain login it would wipe the buffs they earned out in the world.
            Compat.RpgClassesCompat.ResetCombat(player.Entity);
            player.SendMessage(0, Lang.Get("canevents:inventory-restored"), EnumChatType.Notification);
        }

        // Idempotent. Offline players get it on next login.
        private void RefundIfNoLongerCommitted(string uid)
        {
            if (!(sapi.World.PlayerByUid(uid) is IServerPlayer player)) return;
            if (!EntryFeeEscrow.Has(player)) return;

            string code = EntryFeeEscrow.EventCodeOf(player);
            if (!string.IsNullOrEmpty(code))
            {
                var ev = FindEventByCode(code);
                if (ev != null && _store.HasPlayerRegistration(uid, ev)) return;            // still queued for it
                // In its live match. Not HoldsPlayer: a released loser still pays into the pot at the end.
                if (RunningEventsSnapshot().Any(re => re.Event.CodeEquals(code)
                        && re.IsParticipant(uid) && !re.Roster.IsPreStartDropout(uid))) return;
            }
            RewardDistributor?.RefundEntryFeeEscrow(player);
        }

        private void BroadcastToParticipants(RunningEvent runningEvent, string message)
            => eventBehavior.Shared.EventBroadcaster.Broadcast(runningEvent, message);

        private void BroadcastToServer(string message)
            => eventBehavior.Shared.EventBroadcaster.BroadcastToServer(sapi, message);
    }
}
