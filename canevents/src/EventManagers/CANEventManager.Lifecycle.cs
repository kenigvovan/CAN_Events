using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers.eventBehavior.Shared;
using canevents.src.Network;
using canevents.src.Scoring;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace canevents.src.EventManagers
{
    // Event lifecycle: finding an arena, countdown + start, tick/finish logic.
    public partial class CANEventManager
    {
        public void AttemptFindLocationForEvent()
        {
            // Snapshot first: the loop below places schematics and finishes matches, both of which
            // reach back into the live list.
            var searching = RunningEventsSnapshot()
                .Where(re => re.EventState == EventState.SearchingForLocation)
                .ToList();

            // Tick interval, used to age the search timeout below.
            float intervalSeconds = Math.Max(1, config.attemptFindLocationForRunningEventEveryNSeconds);

            foreach (var re in searching)
            {
                // Re-check: may have been finished/cancelled between snapshot and now.
                if (re.EventState != EventState.SearchingForLocation) continue;

                if (!eventPlacesManager.TryFindEventPlace(re.Event.EventTypes.ToArray(), out var place, re.AllowedPlaceIds))
                {
                    sapi.World.Logger.Error("Failed to find event place for event {0}", re.Event.Code);

                    // Give up after the timeout instead of retrying forever: a match whose only arena
                    // was deleted keeps its players busy and holds their escrow until a restart.
                    re.SearchingSeconds += intervalSeconds;
                    if (config.maxLocationSearchSeconds > 0
                        && re.SearchingSeconds >= config.maxLocationSearchSeconds)
                    {
                        AbortSearchingEvent(re, "no free arena found within the search timeout");
                    }
                    continue;
                }

                // Heavy block writes, deliberately outside the lock.
                if (place.SchematicOnDemand && place.SchematicOrigin != null)
                {
                    var template = ArenaTemplates.Find(t => t.Code == place.SchematicTemplateCode);
                    if (template == null)
                    {
                        sapi.World.Logger.Error("On-demand place {0} references missing template '{1}'", place.Name, place.SchematicTemplateCode);
                        continue;
                    }
                    if (!SchematicPlacer.Place(template, place.SchematicOrigin, place.Id))
                    {
                        sapi.World.Logger.Error("On-demand schematic place failed for {0}", place.Name);
                        continue;
                    }
                }

                re.Place = place;
                // Hold the arena in memory for the whole match: a bot-only line-up has no player
                // nearby to keep the columns loaded, and the ground would unload under the fight.
                re.PinnedChunks = EventPlacesManagers.ArenaChunkPin.Pin(sapi, place);
                re.EventState = EventState.LocationFound;
                re.InitializeRoster();
                place.Occupied = true;
                sapi.World.Logger.Notification("Found event place for event {0} at {1}", re.Event.Code, place.Name);
                TreeAttribute tree = new TreeAttribute();
                tree.SetLong("guid", re.Id);
                sapi.World.Api.Event.PushEvent("eventLocationFound", tree);
                NetworkHandler?.BroadcastEventList();
            }
        }

        // Cancels a match that assembled but never found an arena. OnBeforeStart never ran, so only the
        // held entry-fee stakes need refunding - no inventory restore or arena teardown. Offline players
        // are refunded on reconnect via RefundIfNoLongerCommitted.
        private void AbortSearchingEvent(RunningEvent re, string reason)
        {
            sapi.World.Logger.Warning("Event {0} (id {1}) aborted while searching for a location: {2}",
                re.Event.Code, re.Id, reason);

            re.EventState = EventState.Finished;

            foreach (var pl in re.OnlineParticipants)
            {
                if (re.Event.EntryFee != null && EntryFeeEscrow.Has(pl)
                    && re.Event.CodeEquals(EntryFeeEscrow.EventCodeOf(pl)))
                {
                    RewardDistributor?.RefundEntryFeeEscrow(pl);
                }
                pl.SendMessage(0, Lang.Get("canevents:event-aborted-no-arena", re.Event.Code),
                    EnumChatType.Notification);
            }

            // Observers (the tournament driver) must hear about this, or a bracket match whose arena
            // vanished sits in "live" forever - no result will ever arrive.
            log.Safely("OnMatchAborted observer for " + re.Event.Code,
                () => OnMatchAborted?.Invoke(re, reason));

            RemoveRunningEvent(re);
            NetworkHandler?.BroadcastEventList();
        }

        public void StartEvent(string eventName, ref EnumHandling handling, IAttribute data)
        {
            TreeAttribute tree = data as TreeAttribute;
            long reGuid = tree.GetLong("guid");

            var runningEvent = FindRunningEvent(reGuid);
            if (runningEvent == null)
            {
                sapi.World.Logger.Warning("eventLocationFound fired for event id {0} but RunningEvent is no longer in the live match list (likely cancelled or already finished)", reGuid);
                return;
            }

            runningEvent.SetDuration(config.maxEventDurationSeconds);

            // Remove cage preview blocks so BuildCage saves the real original terrain
            if (runningEvent.Place != null)
                NetworkHandler?.RestoreCagePreview(runningEvent.Place.Id);

            DispatchToBehaviors(runningEvent, "OnBeforeStart", beh => beh.OnBeforeStart());

            int countdown = Math.Max(config.countdownSeconds, 10);
            NetworkHandler?.RequestLoadoutPicks(runningEvent, countdown);

            BroadcastToParticipants(runningEvent, Lang.Get("canevents:event-starting", runningEvent.Event.Code, countdown));

            runningEvent.CountdownSeconds = countdown;
            long countdownId = sapi.Event.RegisterGameTickListener((float dt) =>
            {
                runningEvent.CountdownSeconds -= dt;
                if (runningEvent.CountdownSeconds <= 0)
                {
                    runningEvent.CountdownSeconds = 0;
                    // Unregister FIRST, and guard the start: a throw from ActuallyStartEvent before the
                    // unregister left this listener firing every 500 ms, re-starting the event with
                    // duplicate tick listeners and an inventory backup overwritten by the loadout.
                    if (runningEvent.CountdownListenerId >= 0)
                    {
                        sapi.Event.UnregisterGameTickListener(runningEvent.CountdownListenerId);
                        runningEvent.CountdownListenerId = -1;
                    }
                    try { ActuallyStartEvent(runningEvent); }
                    catch (Exception ex)
                    {
                        sapi.World.Logger.Error("ActuallyStartEvent for event {0}: {1}", runningEvent.Event.Code, ex);
                        // The salvage attempt is allowed to fail, but not silently: if it does, this
                        // match stays in the live match list with its arena marked occupied and its
                        // players in the loadout, and the reason has to be in the log.
                        log.Safely("FinishEvent (salvage) for " + runningEvent.Event.Code,
                            () => FinishEvent(runningEvent));
                    }
                }
            }, 500);
            runningEvent.CountdownListenerId = countdownId;

            runningEvent.EventState = EventState.LocationFound; // still in pre-start
            NetworkHandler?.SendStateUpdate(runningEvent);
        }

        /// <summary>Health/speed mutators for everyone actually in the arena. A pre-start dropout is
        /// skipped: they never enter, and a speed stat left on them would outlive the match.</summary>
        private void ApplyStatMutators(RunningEvent runningEvent)
        {
            foreach (var player in runningEvent.OnlineParticipants)
            {
                if (!runningEvent.IsActive(player.PlayerUID)) continue;
                EventBroadcaster.ApplyStatMutators(player, runningEvent.Event);
            }
        }

        private void ActuallyStartEvent(RunningEvent runningEvent)
        {
            // Everyone disconnected during countdown → cancel.
            if (runningEvent.ParticipantsStillInEvent.Count == 0 || runningEvent.EventState == EventState.Finished)
            {
                FinishEvent(runningEvent);
                return;
            }

            // Entry fees were charged into escrow at queue time; nothing to collect here. Settled at
            // match end by RewardDistributor.

            DispatchToBehaviors(runningEvent, "OnStart", beh => beh.OnStart());

            // Player-level mutators (health, speed) after every OnStart: a mode that heals its
            // participants would otherwise wipe a reduced start health, and modes with no loadout
            // at all (spleef, parkour) never pass through the kit path that would apply them.
            log.Safely("ApplyStatMutators for " + runningEvent.Event.Code,
                () => ApplyStatMutators(runningEvent));

            foreach (var beh in runningEvent.Behaviors)
            {
                if (beh.RegisterGameTickListener)
                {
                    // Isolated: an unguarded throw from OnTick reaches the engine's tick loop with no
                    // event context attached.
                    var tickBeh = beh; // capture per iteration
                    long listenerId = sapi.Event.RegisterGameTickListener(dt =>
                    {
                        log.Safely(tickBeh.Name + ".OnTick for " + runningEvent.Event.Code,
                            () => tickBeh.OnTick(dt));
                    }, 1000);
                    runningEvent.RegisteredTickListeners.Add(listenerId);
                }
            }

            // Duration timer + finish check + spectator bounds.
            long finishCheckId = sapi.Event.RegisterGameTickListener((float dt) =>
            {
                runningEvent.ElapsedSeconds += dt;

                if (runningEvent.ElapsedSeconds >= runningEvent.MaxDurationSeconds)
                {
                    runningEvent.RequestFinish("time limit reached");
                }

                // Keep spectators near the arena. Isolated so a SpectatorManager bug
                // can't stall ShouldFinish handling below.
                log.Safely("EnforceSpectatorBounds for " + runningEvent.Event.Code,
                    () => SpectatorManager?.EnforceSpectatorBounds(runningEvent));

                // Broadcast telemetry for the observers, if any are watching this match.
                log.Safely("SendSpectatorTelemetry for " + runningEvent.Event.Code,
                    () => NetworkHandler?.SendSpectatorTelemetry(runningEvent));

                if (runningEvent.ShouldFinish)
                {
                    log.Safely("FinishEvent for " + runningEvent.Event.Code, () => FinishEvent(runningEvent));
                }
            }, 500);
            runningEvent.RegisteredTickListeners.Add(finishCheckId);

            // Arena outline, once a second: particles live 1.2s (see ZoneVisualizer.MakeProps), so
            // the border never blinks between redraws. Its own listener rather than a branch in the
            // 500 ms tick above, which would draw it twice as often as it lasts.
            if (ShouldOutlineArena(runningEvent))
            {
                runningEvent.RegisterMatchTick(_ =>
                    ZoneVisualizer.DrawBox(sapi, runningEvent.Place.BoundsMin, runningEvent.Place.BoundsMax,
                        ZoneVisualizer.ArenaBorderColor), 1000);
            }

            runningEvent.EventState = EventState.Running;
            BroadcastToParticipants(runningEvent, Lang.Get("canevents:event-started", runningEvent.Event.Code));
            sapi.World.Logger.Notification("Event {0} started", runningEvent.Event.Code);

            // Isolated: an admin who cannot be seated must not take the match down with them.
            log.Safely("AutoObserveOnStart for " + runningEvent.Event.Code,
                () => AutoObserveOnStart(runningEvent));

            NetworkHandler?.SendStateUpdate(runningEvent);
            NetworkHandler?.BroadcastEventList();
        }

        // Does this match want its arena footprint outlined? Only when there is a footprint to draw
        // and the mode is not already drawing an edge of its own (see IDrawsOwnBoundary).
        private static bool ShouldOutlineArena(RunningEvent runningEvent)
        {
            var place = runningEvent.Place;
            if (place == null || !place.HasBounds || place.BoundsMin == null || place.BoundsMax == null)
                return false;

            return runningEvent.GetBehavior<IDrawsOwnBoundary>() == null;
        }

        public void FinishEvent(RunningEvent runningEvent)
        {
            if (runningEvent.EventState == EventState.Finished) return;
            runningEvent.EventState = EventState.Finished;

            // Last chance for behaviors to declare winners before CaptureResult reads them. Lives here,
            // not at the tick-loop call site, because FinishEvent has several callers (e.g. a match
            // abandoned during the countdown) which would otherwise skip the hook silently.
            DispatchToBehaviors(runningEvent, "OnBeforeFinish", beh => beh.OnBeforeFinish());

            // Freeze the outcome BEFORE teardown: CleanupRunningEvent runs behavior OnEnd(), which may
            // wipe scoring state. Reward delivery must happen after it (drops should land at the
            // post-teleport position), hence capture here and apply from the snapshot afterwards.
            var result = CaptureResult(runningEvent);
            string winnerNames = result.Winners.Count > 0
                ? string.Join(", ", result.Winners.Select(NameOf))
                : "No one";

            CleanupRunningEvent(runningEvent);
            RecordMatchResult(runningEvent, result);

            // After recording, so observers see the same world state the leaderboard does. Isolated:
            // a throwing observer must not strand the match half-finished.
            log.Safely("OnMatchFinished observer for " + runningEvent.Event.Code,
                () => OnMatchFinished?.Invoke(runningEvent, result));

            BroadcastToServer(Lang.Get("canevents:event-finished", runningEvent.Event.Code, winnerNames));
            sapi.World.Logger.Notification("Event {0} finished. Winner: {1}", runningEvent.Event.Code, winnerNames);

            NetworkHandler?.SendStateUpdate(runningEvent);
            NetworkHandler?.BroadcastEventList();

            // Last, so the observers still received the result and the closing state packet above.
            ReleaseObservers(runningEvent);

            RemoveRunningEvent(runningEvent);
        }

        private void CleanupRunningEvent(RunningEvent runningEvent)
        {
            SpectatorManager?.RemoveAllSpectators(runningEvent);

            foreach (var pl in runningEvent.OnlineParticipants)
            {
                if (!pl.Entity.Alive) pl.Entity.Revive();
                // A knock-down mod may have left someone crawling and immobilized. The match is over,
                // so nothing will come along to lift that - and TPThereAndBack is about to send them
                // home in whatever state they are in.
                Compat.DownedCompat.Clear(pl);
                // The speed mutator, dropped here rather than in a behavior teardown: every mode
                // passes through this loop, including the ones that hand out no kit at all. Someone
                // offline by now needs nothing - the stat is non-persistent and dies with the entity.
                EventBroadcaster.ClearSpeedMutator(pl);
            }

            DispatchToBehaviors(runningEvent, "OnEnd", beh => beh.OnEnd());

            // Issued kit left on the floor outlives the match otherwise. Placed exactly here on
            // purpose: after OnEnd, so inventories are restored and the players already teleported
            // home; before the chunk pin is released below, because a released chunk can unload and
            // the search would find nothing; and before RecordMatchResult, whose reward drops land
            // at the players' feet at home and must never be swept.
            if (PolicyOf(runningEvent) != eventBehavior.Shared.IssuedGearDrop.Allow)
                log.Safely("issued gear sweep", () =>
                    eventBehavior.Shared.IssuedGearSweep.Sweep(sapi, runningEvent.Place, log));

            foreach (var id in runningEvent.RegisteredTickListeners)
                sapi.Event.UnregisterGameTickListener(id);
            runningEvent.RegisteredTickListeners.Clear();

            if (runningEvent.CountdownListenerId >= 0)
            {
                sapi.Event.UnregisterGameTickListener(runningEvent.CountdownListenerId);
                runningEvent.CountdownListenerId = -1;
            }

            if (runningEvent.PinnedChunks != null)
            {
                EventPlacesManagers.ArenaChunkPin.Release(sapi, runningEvent.PinnedChunks);
                runningEvent.PinnedChunks = null;
            }

            if (runningEvent.Place != null)
            {
                runningEvent.Place.Occupied = false;
                if (runningEvent.Place.SchematicOnDemand && SchematicPlacer != null
                    && SchematicPlacer.HasSavedData(runningEvent.Place.Id))
                {
                    SchematicPlacer.Restore(runningEvent.Place.Id);
                }
            }
        }

        // Freezes the match outcome while behavior state is still live. MUST run before
        // CleanupRunningEvent, because behaviors clear their state in OnEnd().
        private MatchResult CaptureResult(RunningEvent runningEvent)
        {
            var scorer = runningEvent.Scorer;
            return new MatchResult(
                runningEvent.GetWinners(),
                scorer?.GetFinalRanking(),
                scorer?.GetFinalKillCounts(),
                scorer?.GetFinalDeathCounts());
        }

        // Applies the frozen result: rewards, standings, result screen, history.
        private void RecordMatchResult(RunningEvent runningEvent, MatchResult result)
            => resultRecorder.Record(runningEvent, result);

        /// <summary>Display name for a uid, falling back to the uid itself when the player is offline.
        /// A bot is named from its uid, so every surface calls it the same thing.</summary>
        internal string NameOf(string uid)
            => Bots.BotIdentity.IsBot(uid)
                ? Bots.BotIdentity.NameOfUid(uid)
                : sapi.World.PlayerByUid(uid)?.PlayerName ?? uid;
    }
}
