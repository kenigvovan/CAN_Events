using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace canevents.src.EventManagers
{
    // Per-player event hooks: death, respawn, disconnect, reconnect.
    // Also holds the two broadcast helpers used by Lifecycle.
    public partial class CANEventManager
    {
        public void OnDeath(IServerPlayer player, DamageSource source)
        {
            foreach (var re in RunningEventsSnapshot())
            {
                if (re.EventState != EventState.Running) continue;
                if (re.IsActive(player.PlayerUID))
                {
                    DispatchToBehaviors(re, "OnParticipantDeath", beh => beh.OnParticipantDeath(player, source));

                    // If player was eliminated, make them spectator
                    if (!re.IsActive(player.PlayerUID))
                    {
                        if (SpectatesOnElimination(re)) SpectatorManager?.MakeSpectator(player, re);
                        else ReleaseOnElimination(player, re);

                        // Rated track only, never for an exhibition match or a bot - same guards as the
                        // kill side, so standings can't be seeded from unrated play or bot rows.
                        if (re.Event.Rated && !re.SuppressRating && !Bots.BotIdentity.IsBot(player.PlayerUID))
                            Leaderboard?.RecordDeath(player.PlayerUID, re.Event.Code);
                    }
                }
            }
        }

        public void OnRespawn(IServerPlayer player)
        {
            foreach (var re in RunningEventsSnapshot())
            {
                if (re.EventState != EventState.Running) continue;
                if (!re.IsParticipant(player.PlayerUID)) continue;

                // Player was eliminated but respawned naturally (clicked Respawn button)
                // while the event is still running → redirect to spectator
                if (!re.IsActive(player.PlayerUID))
                {
                    if (SpectatesOnElimination(re)) SpectatorManager?.ApplySpectatorState(player, re);
                    // Not seated by this event: clicking Respawn is their way out, so hand their own
                    // gear back and put them where they came from rather than at the world spawn.
                    else TryReleaseEliminated(player);
                }

                // Opt this revive out of vanilla satiety handling: EntityBehaviorHunger halves
                // saturation and all nutrient levels (which drive max health) on Heal/Revive. That
                // fires inside the TeleportTo callback, which can land either side of the 200ms
                // dispatch below, so racing it with our own Feed() is a coin flip. The flag makes the
                // engine skip the block; it is consumed by that one revive.
                player.Entity?.Attributes.SetBool("noSatietyRestoreOnRevive", true);

                // The engine runs its own respawn logic (clear inventory, teleport to world spawn)
                // AFTER PlayerRespawn fires, so a synchronous dispatch gets clobbered next tick.
                string uid = player.PlayerUID;
                var capturedRe = re;
                sapi.Event.RegisterCallback(_ =>
                {
                    var pl = sapi.World.PlayerByUid(uid) as IServerPlayer;
                    if (pl == null) return;
                    if (capturedRe.EventState != EventState.Running) return;
                    if (!capturedRe.IsParticipant(uid)) return;
                    DispatchToBehaviors(capturedRe, "OnParticipantRespawn", beh => beh.OnParticipantRespawn(pl));
                }, 200);
            }
        }

        public void OnDisconnect(IServerPlayer player)
        {
            // Stop pushing bracket updates to a client that is gone.
            NetworkHandler?.DropBracketViewer(player.PlayerUID);

            foreach (var re in RunningEventsSnapshot())
            {
                if (re.IsActive(player.PlayerUID))
                {
                    re.EliminatePlayer(player.PlayerUID);
                    BroadcastToParticipants(re, Lang.Get("canevents:player-disconnected-eliminated",
                        player.PlayerName, re.ParticipantsStillInEvent.Count));

                    DispatchToBehaviors(re, "OnParticipantLeftGame", beh => beh.OnParticipantLeftGame());
                }
                // Disconnected before the match found an arena: the roster isn't initialized yet, so
                // EliminatePlayer would no-op. Mark a pre-start dropout so InitializeRoster leaves them
                // out - otherwise the offline uid can win (or deadlock) the match.
                else if (re.EventState == EventState.SearchingForLocation
                    && re.IsParticipant(player.PlayerUID))
                {
                    re.MarkPreStartDropout(player.PlayerUID);
                }

                // Park the stake NOW, while the escrow ModData is still reachable: once fully offline
                // PlayerByUid returns null and settlement can't read it, letting a player disconnect to
                // reclaim the stake instead of feeding the pot. A real crash fires no PlayerDisconnect,
                // so the escrow survives and genuine crash victims are refunded on reconnect.
                // Keyed on IsParticipant, not IsActive: an already-eliminated player still owes the pot.
                if (re.IsParticipant(player.PlayerUID)
                    && re.Event.EntryFee != null
                    && re.Event.CodeEquals(EntryFeeEscrow.EventCodeOf(player)))
                {
                    var stake = EntryFeeEscrow.Read(player);
                    if (stake != null) RewardDistributor?.RecordForfeit(player.PlayerUID, re.Id, stake);
                    EntryFeeEscrow.Clear(player);
                }

                // No RemoveSpectator here: the player is going offline so BroadcastPlayerData won't
                // work. ModData is saved; RestoreOnReconnect fixes the gamemode when they rejoin.
            }

            // Stop feeding a match's HUD to an observer who is gone. No packet to send: the client is
            // already disconnecting, and the gamemode is restored from ModData on their next login.
            if (DropObserverFromMatches(player.PlayerUID))
                NetworkHandler?.ForgetSpectator(player.PlayerUID);

            // Drop the in-memory spectator anchor so a mid-event reconnect goes through the
            // normal restore path instead of being treated as still-spectating.
            SpectatorManager?.OnSpectatorDisconnect(player.PlayerUID);

            // An abandoned template-edit session would keep its preview arena and session entry
            // alive until a restart; treat disconnect as Cancel (blocks restored, place removed).
            if (TemplateEditor != null && TemplateEditor.Sessions.ContainsKey(player.PlayerUID))
            {
                log.Safely("TemplateEditor.Cancel on disconnect for " + player.PlayerName,
                    () => TemplateEditor.Cancel(player));
            }

            // Drop pending queue registrations so an offline player can't be matched.
            DropQueueRegistrationsForPlayer(player.PlayerUID);

            // Same for an outstanding duel challenge: a side that just walked out cannot fight, and
            // the other party deserves to hear why rather than wait out the timeout.
            sapi.ModLoader.GetModSystem<canevents>()?.Duels?.OnDisconnect(player.PlayerUID);
        }

        public void OnReconnect(IServerPlayer player)
        {
            SpectatorManager?.RestoreOnReconnect(player);

            foreach (var re in RunningEventsSnapshot())
            {
                if (re.IsParticipant(player.PlayerUID))
                    DispatchToBehaviors(re, "OnParticipantJoinedGame", beh => beh.OnParticipantJoinedGame());
            }

            // Restore a SavePlayerAttributes backup, but only when they are no longer PLAYING: a
            // live-match reconnect must keep the backup, or a later in-match death would ClearInventory
            // with nothing left to restore - permanent loss. An eliminated player, by contrast, would
            // otherwise walk the world carrying the arena kit and could stash it before the match ends.
            if (!IsPlayingLiveEvent(player.PlayerUID) && !(SpectatorManager?.IsSpectating(player.PlayerUID) ?? false))
                RestoreEventBackup(player);

            // Refund an escrow the player is no longer committed to (crash, or a queue slot dropped
            // while offline). A live queue slot or running match keeps it for normal settlement.
            RefundIfNoLongerCommitted(player.PlayerUID);

            // Hand back a stake forfeited by a mid-match disconnect if its match is no longer running
            // (crash before settlement, or a void match). A running match keeps it committed.
            RewardDistributor?.RefundForfeitIfStale(player,
                mid => FindRunningEvent(mid) != null);

            // Anything a match owed them while they were offline. Rewards are handed out after the
            // last teleport of a match, so the player who just lost and closed the game is precisely
            // the one who misses them.
            int paid = RewardDistributor?.Payouts?.Deliver(player, "reward") ?? 0;
            if (paid > 0)
                player.SendMessage(0, Lang.Get("canevents:payout-delivered"), EnumChatType.Notification);

            // Send them back to their pre-event position if a crash stranded them in an arena
            // (TPThereAndBack.OnEnd never ran).
            RestoreOrphanedReturnPosition(player);

            // Tournament business: prizes/refunds owed while they were offline, and a re-prompt if
            // their bracket match is still waiting on them.
            Tournaments?.OnReconnect(player);
        }

        // Crash recovery: a TPThereAndBack return-position marker with no running event owning the
        // player means the event was torn down without OnEnd. Teleport them back, then clear the
        // marker. A live event owning the player takes precedence.
        private void RestoreOrphanedReturnPosition(IServerPlayer player)
        {
            byte[] data = player.GetModdata(eventBehavior.TPThereAndBack.ReturnPosModDataKey);
            if (data == null || data.Length == 0) return;

            string uid = player.PlayerUID;
            if (IsInLiveEvent(uid))
                return; // a live event still owns their position, let its normal flow handle it

            double x, y, z;
            try
            {
                var tree = new TreeAttribute();
                tree.FromBytes(data);
                x = tree.GetInt("x") + 0.5;
                y = tree.GetInt("y") + 0.5;
                z = tree.GetInt("z") + 0.5;
            }
            catch (Exception e)
            {
                sapi.World.Logger.Warning("RestoreOrphanedReturnPosition for {0}: {1}", player.PlayerName, e.Message);
                player.RemoveModdata(eventBehavior.TPThereAndBack.ReturnPosModDataKey);
                return;
            }

            // Defer so the engine's join-time positioning settles first. The marker is cleared only once
            // the teleport lands, so a disconnect inside the window retries.
            sapi.Event.RegisterCallback(_ =>
            {
                var pl = sapi.World.PlayerByUid(uid) as IServerPlayer;
                if (pl?.Entity == null) return;
                pl.Entity.TeleportToDouble(x, y, z);
                pl.RemoveModdata(eventBehavior.TPThereAndBack.ReturnPosModDataKey);
            }, 300);
            sapi.World.Logger.Notification("Returning {0} to pre-event position after orphaned event (crash recovery)", player.PlayerName);
        }

        /// <summary>Does this match sit its eliminated players in the spectator seat? Set per event
        /// on SavePlayerAttributes; an event without that behavior keeps the historic behavior.</summary>
        private bool SpectatesOnElimination(RunningEvent re)
            => re?.GetBehavior<eventBehavior.SavePlayerAttributes>()?.SpectateOnElimination ?? true;

        /// <summary>
        /// Sends a just-eliminated player home instead of seating them, for an event that would
        /// rather not make people watch.
        /// </summary>
        private void ReleaseOnElimination(IServerPlayer player, RunningEvent re)
        {
            string uid = player.PlayerUID;
            // Delayed for the same reason MakeSpectator is: this runs inside TriggerPlayerDeath, and
            // reviving before the engine has finished announcing the death leaves them half-dead.
            sapi.Event.RegisterCallback(_ =>
            {
                // The match ending first has already restored and teleported everyone.
                if (re.EventState == EventState.Finished) return;

                var pl = sapi.World.PlayerByUid(uid) as IServerPlayer;
                if (pl?.Entity == null) return;
                if (!pl.Entity.Alive) pl.Entity.Revive();

                TryReleaseEliminated(pl);
            }, 100);
        }

        /// <summary>
        /// Releases a player who is already out of the match: their own inventory comes back, they
        /// go home and the spectator seat is freed, without waiting for the match to end. They stay
        /// on the roster, so rewards and the results table still count them.
        ///
        /// Watching the rest of the match is a thing to offer, not to impose - a one-life mode can
        /// leave someone sitting there for the better part of ten minutes.
        ///
        /// False when they are still in play: that is a live match, and walking out of one would be
        /// a free escape from a fight going badly.
        /// </summary>
        public bool TryReleaseEliminated(IServerPlayer player)
        {
            if (player == null) return false;
            string uid = player.PlayerUID;

            var re = FindLiveEventOf(uid);
            if (re == null || re.IsActive(uid)) return false;

            // Their real belongings first: everything else can be redone, this cannot.
            RestoreEventBackup(player);

            // Home, and only once - the match's own teleport must not drag them back later.
            re.GetBehavior<eventBehavior.TPThereAndBack>()?.ForgetReturnPos(uid);
            if (TryReadReturnPos(player, out double x, out double y, out double z))
            {
                player.Entity?.TeleportToDouble(x, y, z);
                player.RemoveModdata(eventBehavior.TPThereAndBack.ReturnPosModDataKey);
            }

            SpectatorManager?.RemoveSpectator(player);
            return true;
        }

        /// <summary>The pre-event position mirrored to ModData at teleport-in, if it is still there.</summary>
        private bool TryReadReturnPos(IServerPlayer player, out double x, out double y, out double z)
        {
            x = y = z = 0;
            byte[] data = player?.GetModdata(eventBehavior.TPThereAndBack.ReturnPosModDataKey);
            if (data == null || data.Length == 0) return false;

            try
            {
                var tree = new TreeAttribute();
                tree.FromBytes(data);
                x = tree.GetInt("x") + 0.5;
                y = tree.GetInt("y") + 0.5;
                z = tree.GetInt("z") + 0.5;
                return true;
            }
            catch (Exception e)
            {
                sapi.World.Logger.Warning("Return position for {0} is unreadable: {1}", player.PlayerName, e.Message);
                player.RemoveModdata(eventBehavior.TPThereAndBack.ReturnPosModDataKey);
                return false;
            }
        }

        // Crash recovery for inventory + health. Shares PlayerInventoryBackup.Restore with the normal
        // end-of-event path so the two can't drift. Only this path notifies the player.
        private void RestoreEventBackup(IServerPlayer player)
        {
            if (PlayerInventoryBackup.Restore(player, sapi))
                player.SendMessage(0, Lang.Get("canevents:inventory-restored"), EnumChatType.Notification);
        }

        // Refunds a held escrow when the player is neither queued for its event nor in its running
        // match. Idempotent: it re-checks state, so a repeated call is harmless. Offline players are
        // skipped and refunded when they next log in.
        private void RefundIfNoLongerCommitted(string uid)
        {
            if (!(sapi.World.PlayerByUid(uid) is IServerPlayer player)) return;
            if (!EntryFeeEscrow.Has(player)) return;

            string code = EntryFeeEscrow.EventCodeOf(player);
            if (!string.IsNullOrEmpty(code))
            {
                var ev = FindEventByCode(code);
                if (ev != null && _store.HasPlayerRegistration(uid, ev)) return;            // still queued for it
                if (RunningEventsSnapshot().Any(re => re.Event.CodeEquals(code)
                        && re.IsParticipant(uid))) return;                     // in its live match
            }
            RewardDistributor?.RefundEntryFeeEscrow(player);
        }

        private void BroadcastToParticipants(RunningEvent runningEvent, string message)
            => eventBehavior.Shared.EventBroadcaster.Broadcast(runningEvent, message);

        private void BroadcastToServer(string message)
            => eventBehavior.Shared.EventBroadcaster.BroadcastToServer(sapi, message);
    }
}
