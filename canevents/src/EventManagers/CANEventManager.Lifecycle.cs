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
            var searching = RunningEventsSnapshot()
                .Where(re => re.EventState == EventState.SearchingForLocation)
                .ToList();

            float intervalSeconds = Math.Max(1, config.attemptFindLocationForRunningEventEveryNSeconds);

            foreach (var re in searching)
            {
                if (re.EventState != EventState.SearchingForLocation) continue;

                if (!eventPlacesManager.TryFindEventPlace(re.Event.EventTypes.ToArray(), out var place, re.AllowedPlaceIds))
                {
                    sapi.World.Logger.Error("Failed to find event place for event {0}: {1}",
                        re.Event.Code, AvailabilityOf(re.Event, re.AllowedPlaceIds).Summary);

                    CountSearchTimeout(re, intervalSeconds);
                    continue;
                }

                // Bot-only matches have no player to keep chunks loaded. Pin before on-demand placement.
                re.PinnedChunks = config.pinArenaChunks
                    ? EventPlacesManagers.ArenaChunkPin.Pin(sapi, place)
                    : null;

                if (place.SchematicOnDemand && place.SchematicOrigin != null)
                {
                    var template = ArenaTemplates.Find(t =>
                        string.Equals(t.Code, place.SchematicTemplateCode, StringComparison.OrdinalIgnoreCase));
                    bool placed = false;
                    if (template == null)
                        sapi.World.Logger.Error("On-demand place {0} references missing template '{1}'", place.Name, place.SchematicTemplateCode);
                    else if (SchematicPlacer == null)
                        sapi.World.Logger.Error("On-demand place {0}: no schematic placer wired", place.Name);
                    else
                    {
                        // Anything but Placed retries next pass.
                        var outcome = SchematicPlacer.TryPlace(template, place.SchematicOrigin, place.Id);
                        placed = outcome == EventPlacesManagers.SchematicPlaceResult.Placed;
                        if (outcome == EventPlacesManagers.SchematicPlaceResult.ChunksNotLoaded)
                            sapi.World.Logger.VerboseDebug("On-demand place {0}: waiting for its chunks to load", place.Name);
                        else if (!placed)
                            sapi.World.Logger.Warning("On-demand schematic place for {0} not done ({1}), retrying next pass",
                                place.Name, outcome);
                    }

                    if (!placed)
                    {
                        // Next pass may pick another arena; unload lingers so a re-pin keeps the chunks.
                        ReleasePinnedChunks(re);
                        CountSearchTimeout(re, intervalSeconds);
                        continue;
                    }
                }

                re.Place = place;
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

        private void CountSearchTimeout(RunningEvent re, float intervalSeconds)
        {
            re.SearchingSeconds += intervalSeconds;
            if (config.maxLocationSearchSeconds > 0
                && re.SearchingSeconds >= config.maxLocationSearchSeconds)
            {
                AbortSearchingEvent(re, "no free arena found within the search timeout");
            }
        }

        // OnBeforeStart never ran, so only escrow needs refunding. Requeued players keep their escrow.
        private void AbortSearchingEvent(RunningEvent re, string reason,
            string messageKey = "canevents:event-aborted-no-arena", ISet<string> requeued = null)
        {
            sapi.World.Logger.Warning("Event {0} (id {1}) aborted while searching for a location: {2}",
                re.Event.Code, re.Id, reason);

            re.EventState = EventState.Finished;

            foreach (var pl in re.OnlineParticipants)
            {
                if (requeued != null && requeued.Contains(pl.PlayerUID)) continue;
                if (re.Event.EntryFee != null && EntryFeeEscrow.Has(pl)
                    && re.Event.CodeEquals(EntryFeeEscrow.EventCodeOf(pl)))
                {
                    RewardDistributor?.RefundEntryFeeEscrow(pl);
                }
                pl.SendMessage(0, Lang.Get(messageKey, re.Event.Code), EnumChatType.Notification);
            }

            // Tournament would otherwise wait forever for a result.
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

            int countdown = ResolveCountdown(runningEvent);
            int lobby = runningEvent.Behaviors.OfType<IPreStartLobby>().Select(l => l.LobbySeconds).DefaultIfEmpty(0).Max();
            if (lobby > 0)
            {
                countdown = Math.Max(countdown, lobby);
            }
            else
            {
                // Extend the countdown only if someone actually got a pick popup.
                int asked = NetworkHandler?.RequestLoadoutPicks(runningEvent, Math.Max(countdown, MinLoadoutPickSeconds)) ?? 0;
                if (asked > 0) countdown = Math.Max(countdown, MinLoadoutPickSeconds);
            }

            BroadcastToParticipants(runningEvent, Lang.Get("canevents:event-starting", runningEvent.Event.Code, countdown));

            runningEvent.CountdownSeconds = countdown;
            long countdownId = sapi.Event.RegisterGameTickListener((float dt) =>
            {
                runningEvent.CountdownSeconds -= dt;
                if (runningEvent.CountdownSeconds <= 0)
                {
                    runningEvent.CountdownSeconds = 0;
                    // Unregister first, or a throw below re-starts the event every 500 ms.
                    if (runningEvent.CountdownListenerId >= 0)
                    {
                        sapi.Event.UnregisterGameTickListener(runningEvent.CountdownListenerId);
                        runningEvent.CountdownListenerId = -1;
                    }
                    try { ActuallyStartEvent(runningEvent); }
                    catch (Exception ex)
                    {
                        sapi.World.Logger.Error("ActuallyStartEvent for event {0}: {1}", runningEvent.Event.Code, ex);
                        log.Safely("FinishEvent (salvage) for " + runningEvent.Event.Code,
                            () => FinishEvent(runningEvent));
                    }
                }
            }, 500);
            runningEvent.CountdownListenerId = countdownId;

            runningEvent.EventState = EventState.LocationFound; // still in pre-start
            NetworkHandler?.SendStateUpdate(runningEvent);
        }

        private const int MinLoadoutPickSeconds = 10;

        // No floor here; MinLoadoutPickSeconds is applied only when a pick popup opens.
        private int ResolveCountdown(RunningEvent runningEvent)
        {
            if (runningEvent.IsOpenMatch) return 0;
            int? own = runningEvent.Event?.CountdownSeconds;
            return Math.Max(0, own ?? config.countdownSeconds);
        }

        // Skips pre-start dropouts: a speed stat on them would outlive the match.
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
            foreach (var lobby in runningEvent.Behaviors.OfType<IPreStartLobby>())
                log.Safely("FinalizeTeams for " + runningEvent.Event.Code, lobby.FinalizeTeams);

            string unplayable = UnplayableReason(runningEvent);
            if (unplayable != null)
            {
                sapi.World.Logger.Notification("Event {0} (id {1}) not started: {2}",
                    runningEvent.Event.Code, runningEvent.Id, unplayable);
                FinishEvent(runningEvent);
                return;
            }

            DispatchToBehaviors(runningEvent, "OnStart", beh => beh.OnStart());

            // After OnStart: a mode that heals on start would wipe a reduced start health.
            log.Safely("ApplyStatMutators for " + runningEvent.Event.Code,
                () => ApplyStatMutators(runningEvent));

            foreach (var beh in runningEvent.Behaviors)
            {
                if (!beh.WantsTick) continue;

                var tickBeh = beh;
                runningEvent.RegisterMatchTick(dt =>
                {
                    log.Safely(tickBeh.Name + ".OnTick for " + runningEvent.Event.Code,
                        () => tickBeh.OnTick(dt));
                }, 1000);
            }

            runningEvent.RegisterMatchTick((float dt) =>
            {
                runningEvent.ElapsedSeconds += dt;

                if (!runningEvent.Unlimited && runningEvent.ElapsedSeconds >= runningEvent.MaxDurationSeconds)
                {
                    runningEvent.RequestFinish("time limit reached");
                }

                log.Safely("EnforceSpectatorBounds for " + runningEvent.Event.Code,
                    () => SpectatorManager?.EnforceSpectatorBounds(runningEvent));

                log.Safely("SendSpectatorTelemetry for " + runningEvent.Event.Code,
                    () => NetworkHandler?.SendSpectatorTelemetry(runningEvent));

                if (runningEvent.ShouldFinish)
                {
                    log.Safely("FinishEvent for " + runningEvent.Event.Code, () => FinishEvent(runningEvent));
                }
            }, 500);

            // Particles live 1.2s (ZoneVisualizer.MakeProps), so a 1s redraw doesn't blink.
            if (ShouldOutlineArena(runningEvent))
            {
                // Configurable: client crashes in the particle pool leave no error. See Config.arenaOutlineParticles.
                int intervalMs = (int)(Math.Max(0.25, config.arenaOutlineIntervalSeconds) * 1000);
                runningEvent.RegisterMatchTick(_ =>
                    ZoneVisualizer.DrawBox(sapi, runningEvent.Place.BoundsMin, runningEvent.Place.BoundsMax,
                        ZoneVisualizer.ArenaBorderColor), intervalMs);
            }

            runningEvent.EventState = EventState.Running;
            BroadcastToParticipants(runningEvent, Lang.Get("canevents:event-started", runningEvent.Event.Code));
            sapi.World.Logger.Notification("Event {0} started", runningEvent.Event.Code);

            // An open match never ends; auto-observing it would hold idle admins forever.
            if (!runningEvent.IsOpenMatch)
                log.Safely("AutoObserveOnStart for " + runningEvent.Event.Code,
                    () => AutoObserveOnStart(runningEvent));

            NetworkHandler?.SendStateUpdate(runningEvent);
            NetworkHandler?.BroadcastEventList();
        }

        // null when the match can start.
        private string UnplayableReason(RunningEvent re)
        {
            if (re.EventState == EventState.Finished) return "already finished";
            // Starts empty on purpose; players walk in later.
            if (re.IsOpenMatch) return re.ShouldFinish ? "a finish was requested before the start" : null;
            if (re.ParticipantsStillInEvent.Count == 0) return "everyone left during the countdown";
            // e.g. Spleef with no floor block gave up in OnBeforeStart.
            if (re.ShouldFinish) return "a finish was requested before the start";
            if (re.ParticipantsByGroupsBefore.Count >= 2 && re.ParticipantsByGroupsStillInEvent.Count <= 1)
                return "only one side is left";
            if (IsQueueMatch(re) && !re.ParticipantsStillInEvent.Any(uid => !Bots.BotIdentity.IsBot(uid)))
                return "no human player is left";
            return null;
        }

        private bool ShouldOutlineArena(RunningEvent runningEvent)
        {
            if (!config.arenaOutlineParticles) return false;

            var place = runningEvent.Place;
            if (place == null || !place.HasBounds || place.BoundsMin == null || place.BoundsMax == null)
                return false;

            return runningEvent.GetBehavior<IDrawsOwnBoundary>() == null;
        }

        public void FinishEvent(RunningEvent runningEvent)
        {
            if (runningEvent.EventState == EventState.Finished) return;
            runningEvent.EventState = EventState.Finished;
            string code = runningEvent.Event?.Code;

            // State is Finished already, nothing will call this again; release must happen in finally.
            try
            {
                DispatchToBehaviors(runningEvent, "OnBeforeFinish", beh => beh.OnBeforeFinish());

                // Capture before OnEnd wipes scoring; rewards go out after teleport home.
                MatchResult result = null;
                log.Safely("CaptureResult for " + code, () => result = CaptureResult(runningEvent));
                result ??= new MatchResult(null, null, null, null);
                string winnerNames = result.Winners.Count > 0
                    ? string.Join(", ", result.Winners.Select(NameOf))
                    : "No one";

                CleanupRunningEvent(runningEvent);
                log.Safely("RecordMatchResult for " + code, () => RecordMatchResult(runningEvent, result));

                log.Safely("OnMatchFinished observer for " + code,
                    () => OnMatchFinished?.Invoke(runningEvent, result));

                log.Safely("finish broadcast for " + code, () =>
                {
                    // An open match has no winner; closing it is not news for the whole server.
                    if (runningEvent.IsOpenMatch)
                        BroadcastToParticipants(runningEvent, Lang.Get("canevents:live-stopped", code));
                    else
                        BroadcastToServer(Lang.Get("canevents:event-finished", code, winnerNames));
                    sapi.World.Logger.Notification("Event {0} finished. Winner: {1}", code, winnerNames);
                    NetworkHandler?.SendStateUpdate(runningEvent);
                    NetworkHandler?.BroadcastEventList();
                });

                // Last, so observers still get the closing state packet.
                log.Safely("ReleaseObservers for " + code, () => ReleaseObservers(runningEvent));
            }
            finally
            {
                if (runningEvent.Place != null) runningEvent.Place.Occupied = false;
                RemoveRunningEvent(runningEvent);
            }
        }

        private void CleanupRunningEvent(RunningEvent runningEvent)
        {
            string code = runningEvent.Event?.Code;
            log.Safely("RemoveAllSpectators for " + code,
                () => SpectatorManager?.RemoveAllSpectators(runningEvent));

            foreach (var pl in runningEvent.OnlineParticipants)
            {
                log.Safely("end-of-match revive for " + pl.PlayerName, () =>
                {
                    if (pl.Entity != null && !pl.Entity.Alive) pl.Entity.Revive();
                    // Downed mod: otherwise they get sent home still crawling.
                    Compat.DownedCompat.Clear(pl);
                    EventBroadcaster.ClearSpeedMutator(pl);
                });
            }

            DispatchToBehaviors(runningEvent, "OnEnd", beh => beh.OnEnd());
            log.Safely("despawn match mobs for " + code, runningEvent.DespawnMobs);

            // Players who died earlier and respawned have a pending fix no respawn will apply.
            foreach (var pl in runningEvent.OnlineParticipants)
                log.Safely("corpse return fix for " + pl.PlayerName,
                    () => CorpseReturnGuard.ApplyPending(sapi, pl));

            // After OnEnd, before chunk unpin and before reward drops land.
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

            ReleasePinnedChunks(runningEvent);

            if (runningEvent.Place != null)
            {
                long placeId = runningEvent.Place.Id;
                // Whatever a behavior failed to put back.
                log.Safely("arena block journal for place " + placeId,
                    () => ArenaBlockJournal.RestoreRemaining(placeId));

                runningEvent.Place.Occupied = false;
                if (runningEvent.Place.SchematicOnDemand && SchematicPlacer != null
                    && SchematicPlacer.HasSavedData(placeId))
                {
                    // A deferred journal restore must land before the terrain comes back.
                    var placer = SchematicPlacer;
                    log.Safely("schematic restore for place " + placeId,
                        () => ArenaBlockJournal.RunAfterRestore(placeId, () => placer.Restore(placeId)));
                }
            }
        }

        private void ReleasePinnedChunks(RunningEvent runningEvent)
        {
            if (runningEvent.PinnedChunks == null) return;
            var pinned = runningEvent.PinnedChunks;
            runningEvent.PinnedChunks = null;
            log.Safely("chunk pin release for " + runningEvent.Event?.Code,
                () => EventPlacesManagers.ArenaChunkPin.Release(sapi, pinned));
        }

        // Must run before CleanupRunningEvent: behaviors clear state in OnEnd.
        private MatchResult CaptureResult(RunningEvent runningEvent)
        {
            var scorer = runningEvent.Scorer;
            return new MatchResult(
                runningEvent.GetWinners(),
                scorer?.GetFinalRanking(),
                scorer?.GetFinalKillCounts(),
                scorer?.GetFinalDeathCounts());
        }

        private void RecordMatchResult(RunningEvent runningEvent, MatchResult result)
            => resultRecorder.Record(runningEvent, result);

        internal string NameOf(string uid) => RunningEvent.NameOf(sapi, uid);
    }
}
