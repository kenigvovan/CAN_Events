using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers.eventBehavior.abstracts;
using canevents.src.EventManagers.eventBehavior.Shared;
using canevents.src.EventManagers.events;
using canevents.src.EventPlacesManagers.Places;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace canevents.src.EventManagers.eventBehavior
{
    // Hunger Games: one-life elimination, FFA by default. Win = last surviving party; time-up goes
    // to the survivor with most kills (ties -> draw). The zone shrinks over phases and damages
    // anyone outside; centre+initial radius come from the place, phases from event attributes so
    // one schedule works across arenas. Kill/death tallies and loadouts live in KillScoringEB.
    public class HungerGamesEB : KillScoringEB, IStartCryProvider
    {

        private readonly ShrinkingZone zone = new ShrinkingZone();
        private double zoneCenterX, zoneCenterY, zoneCenterZ;
        private bool zoneCenterSet = false;
        private int announcedPhaseIdx = -1;

        private readonly HungerGamesLoot loot = new HungerGamesLoot();

        // The border dome is rendered client-side (HgZoneDomeRenderer); the server only sends the
        // current shrink segment on start, per phase and as a slow heartbeat (see HgZoneStateMsg).
        private bool showZoneParticles = true;

        // Reconnect self-heal: a client that joined mid-match missed the last segment packet.
        private const float ZoneStateHeartbeatSeconds = 15f;
        private float lastZoneStateSentAt = float.MinValue;

        // Optional opening truce: no player-vs-player damage for the first graceSeconds (storm and
        // falls still apply). 0 disables it.
        private float graceSeconds = 0f;
        private bool graceActive = false;
        private readonly DamageHookSet graceHooks = new DamageHookSet();

        // Time-up tiebreak below kills: with one life every survivor has zero deaths, so kills alone
        // left too many matches a draw.
        private readonly Dictionary<string, float> damageDealt = new Dictionary<string, float>();
        private readonly DamageHookSet damageHooks = new DamageHookSet();

        // No digging in or towering up: block place/break is denied to live tributes for the whole
        // match (chests open by interaction, so looting is unaffected).
        private bool blockEditsHooked = false;

        // Pedestal freeze: the spawn-ring teleport happens at OnStart (TPThereAndBack runs first),
        // so the hold covers the first startFreezeSeconds of the match; strays are snapped back.
        private float startFreezeSeconds = 5f;
        private readonly Dictionary<string, Vec3d> freezeAnchors = new Dictionary<string, Vec3d>();
        private long freezeListenerId = -1;

        // Feast: at feastAtSeconds the feastTier chests are re-rolled, announced
        // feastAnnounceSeconds ahead so survivors can converge. 0 disables it.
        private float feastAtSeconds = 0f;
        private float feastAnnounceSeconds = 30f;
        private string feastTier = null;
        private bool feastAnnounced, feastOpened;

        // Sponsor gifts: every sponsorEverySeconds a random survivor gets one sponsorTier item
        // dropped from the sky. 0 disables; keep it 0 for ranked, random gifts skew ratings.
        private float sponsorEverySeconds = 0f;
        private string sponsorTier = null;
        private float nextSponsorAt = float.MaxValue;

        // Early storm warning, sent once per upcoming shrink while the zone is still flat.
        private const float StormWarnSeconds = 15f;
        private int warnedPhaseIdx = -1;

        // Death order, first death first: battle-royale placement is survival time, not frags
        // (see GetFinalRanking).
        private readonly SurvivalRanking survival = new SurvivalRanking();

        public HungerGamesEB(string name, JsonObject attributes, RunningEvent runningEvent)
            : base(name, attributes, runningEvent) { }

        public override string Name { get => "HungerGames"; set { } }

        public string StartCryLangKey => "canevents:hungergames-start-cry";

        public override void Initialize()
        {
            ParseTimeLimit(900f);
            showZoneParticles = Config.Bool("showZoneParticles", true);
            graceSeconds = Config.Float("gracePeriodSeconds", 0f);
            startFreezeSeconds = Config.Float("startFreezeSeconds", 5f);
            feastAtSeconds = Config.Float("feastAtSeconds", 0f);
            feastAnnounceSeconds = Config.Float("feastAnnounceSeconds", 30f);
            feastTier = Config.String("feastTier");
            sponsorEverySeconds = Config.Float("sponsorEverySeconds", 0f);
            sponsorTier = Config.String("sponsorTier");
            ParseLoadoutSets();
            zone.ParsePhases(Attributes);
            loot.Parse(Attributes);

            // Zone and loot tables parse their own keys off Attributes; declared here so the
            // unknown-attribute check does not flag them as typos.
            Config.Has("zonePhases");
            Config.Has("lootContainerBlock");
            Config.Has("defaultLootTier");
            Config.Has("minSlotsPerChest");
            Config.Has("maxSlotsPerChest");
            Config.Has("lootTables");
            Config.WarnUnknown();
        }

        public override void OnBeforeStart()
        {
            runningEvent.SetDuration(timeLimitSeconds);
            InitScores();
            survival.Clear();
            damageDealt.Clear();

            ResolveZoneFromPlace();
            loot.Place(runningEvent);

            // From here (countdown included) tributes can neither dig down nor build up.
            if (!blockEditsHooked)
            {
                blockEditsHooked = true;
                runningEvent.sapi.Event.CanPlaceOrBreakBlock += OnCanPlaceOrBreak;
            }
        }

        private bool OnCanPlaceOrBreak(IServerPlayer byPlayer, BlockSelection sel, out string claimant)
        {
            claimant = null;
            if (byPlayer == null || !runningEvent.IsActive(byPlayer.PlayerUID)) return true;
            // Creative mode means an admin testing the arena, not a tribute cheating.
            if (byPlayer.WorldData?.CurrentGameMode == EnumGameMode.Creative) return true;
            claimant = "hungergames";
            byPlayer.SendIngameError("canevents-hg-nobuild", Lang.Get("canevents:hg-no-build"));
            return false;
        }

        // Snap anyone who leaves their pedestal back onto it until the release. Anchors are captured
        // lazily by the tick: the OnStart teleport (TPThereAndBack) is async (waits for chunk
        // loading), so ServerPos here may still be the pre-teleport position.
        private void BeginStartFreeze()
        {
            runningEvent.CombatSuspended = true;
            // MovementFrozen is the bots' hold: CombatSuspended only stops them picking a target,
            // they kept walking and were visibly snapped back by the anchor every 250ms.
            runningEvent.MovementFrozen = true;
            freezeAnchors.Clear();
            // Registered through the match so cleanup unregisters it even if EndStartFreeze never
            // runs; a double-unregister later is harmless.
            freezeListenerId = runningEvent.RegisterMatchTick(FreezeTick, 250);
            EventBroadcaster.Broadcast(runningEvent, Lang.Get("canevents:hg-freeze"));
        }

        private void FreezeTick(float dt)
        {
            // BodyOf, not PlayerByUid: bots stand on pedestals too, and CombatSuspended alone did
            // not stop them walking off.
            foreach (var uid in runningEvent.ParticipantsBefore)
            {
                var entity = runningEvent.BodyOf(uid);
                if (entity == null) continue;
                var pos = entity.Pos;

                FreezeAi(entity);

                if (!freezeAnchors.TryGetValue(uid, out var anchor))
                {
                    freezeAnchors[uid] = pos.XYZ;
                    continue;
                }

                double dx = pos.X - anchor.X;
                double dz = pos.Z - anchor.Z;
                double distSq = dx * dx + dz * dz;

                // >4 blocks in 250ms cannot be movement (sprint-jump covers <2): it is the async
                // pedestal teleport landing, so re-anchor there instead of yanking them back.
                if (distSq > 16.0)
                {
                    freezeAnchors[uid] = pos.XYZ;
                    continue;
                }

                if (distSq > 1.0)
                    entity.TeleportToDouble(anchor.X, anchor.Y, anchor.Z);
            }
        }

        // Stops a bot's AI for this tick. The OnShouldExecuteTask hook (ArenaBotManager.Spawn) only
        // blocks NEW tasks; one already running when the freeze began is never re-asked, so it and
        // its handed-off path traverser / controls movement must be stopped explicitly.
        private void FreezeAi(Entity entity)
        {
            if (entity is not EntityAgent agent || entity is EntityPlayer) return;

            var taskAi = agent.GetBehavior<EntityBehaviorTaskAI>();
            if (taskAi != null)
            {
                taskAi.TaskManager.StopTasks();
                taskAi.PathTraverser?.Stop();
            }

            agent.Controls.StopAllMovement();
            agent.Pos.Motion.X = 0;
            agent.Pos.Motion.Z = 0;
        }

        private void EndStartFreeze()
        {
            runningEvent.MovementFrozen = false;
            if (freezeListenerId >= 0)
            {
                runningEvent.sapi.Event.UnregisterGameTickListener(freezeListenerId);
                freezeListenerId = -1;
            }
            freezeAnchors.Clear();
        }

        private void ResolveZoneFromPlace()
        {
            var place = runningEvent.Place;

            // The radius is this mode's own setting, so it is read here rather than by PlaceZone.
            if (place != null && place.TryGetBehaviorData(BehaviorKeys.HungerGames, out var data)
                && data["InitialRadius"] != null)
            {
                zone.InitialRadius = data.Value<float>("InitialRadius");
            }

            var origin = Shared.PlaceZone.TryResolveCentre(place, BehaviorKeys.HungerGames,
                out zoneCenterX, out zoneCenterY, out zoneCenterZ);
            zoneCenterSet = origin != Shared.ZoneOrigin.None;

            if (origin == Shared.ZoneOrigin.BoundsCentre)
            {
                runningEvent.sapi.World.Logger.Notification(
                    "[HG] Place id={0} has no HungerGames zone center configured; using bounds center.", place.Id);
            }
            else if (origin == Shared.ZoneOrigin.None)
            {
                runningEvent.sapi.World.Logger.Warning(
                    "[HG] No zone center configured for place id={0} and no bounds to fall back to. Storm damage disabled. Use /caneventplace hgzone <radius>.",
                    place?.Id);
            }
        }

        public override void OnStart()
        {
            GiveLoadoutAndHealAll();
            PushScoreboard();
            HookDamageTracking();
            if (graceSeconds > 0f) BeginGrace();
            if (startFreezeSeconds > 0f) BeginStartFreeze();

            // Announce the opening phase here, or the first tick would send a second identical zone
            // packet. Without a zone, clients still need Active=false to clear a leftover dome.
            if (zoneCenterSet && zone.HasPhases) AnnouncePhaseIfChanged(0f);
            else if (showZoneParticles) SendZoneState(true);
        }

        // Tally tribute-vs-tribute damage for the whole match (never modifies it). Grace hits don't
        // count: they're either blocked or shouldn't sway the tiebreak.
        private void HookDamageTracking()
        {
            foreach (var uid in runningEvent.ParticipantsBefore)
            {
                var pl = runningEvent.sapi.World.PlayerByUid(uid) as IServerPlayer;
                if (pl == null) continue;
                string victimUid = uid;
                damageHooks.Hook(pl, (damage, source) =>
                {
                    if (graceActive || damage <= 0f) return damage;
                    if (source?.GetCauseEntity() is EntityPlayer attacker)
                    {
                        string au = attacker.PlayerUID;
                        if (au != victimUid && runningEvent.TeamIndexOf(au) >= 0)
                            damageDealt[au] = (damageDealt.TryGetValue(au, out var d) ? d : 0f) + damage;
                    }
                    return damage;
                });
            }
        }

        // Suspend player-vs-player damage for the opening truce; storm and fall damage pass through.
        // The hook self-disables when the window ends, so a late teardown can't leave players immortal.
        private void BeginGrace()
        {
            graceActive = true;
            runningEvent.CombatSuspended = true;
            foreach (var uid in runningEvent.ParticipantsBefore)
            {
                var pl = runningEvent.sapi.World.PlayerByUid(uid) as IServerPlayer;
                if (pl == null) continue;
                graceHooks.Hook(pl, (damage, source) =>
                {
                    if (!graceActive || damage <= 0f) return damage;
                    // GetCauseEntity() resolves the archer behind an arrow, not the arrow itself.
                    if (source?.GetCauseEntity() is EntityPlayer attacker
                        && runningEvent.TeamIndexOf(attacker.PlayerUID) >= 0)
                        return 0f;
                    return damage;
                });
            }
            EventBroadcaster.Broadcast(runningEvent, Lang.Get("canevents:hg-grace-start", (int)graceSeconds));
        }

        protected override string KillFeedVerb => "eliminated";

        protected override void OnScoredDeath(IServerPlayer victim, KillInfo kill)
        {
            survival.Add(victim.PlayerUID);
            EventBroadcaster.Broadcast(runningEvent,
                Lang.Get("canevents:hg-player-eliminated", victim.PlayerName, kill.KillerName));
        }

        protected override void AfterScoredDeath(IServerPlayer victim, KillInfo kill)
        {
            string victimUid = victim.PlayerUID;

            // One life. EliminatePlayer flips ShouldFinish at <=1 group left and GetWinners() returns
            // that group. CANEventManager.OnDeath handles the spectator handoff after this, so no
            // explicit MakeSpectator here (unlike ParkourEB, which also eliminates outside deaths).
            runningEvent.EliminatePlayer(victimUid);

            // The cannon: a boom the whole arena hears, plus the "N tributes remain" tally.
            var deathPos = victim.Entity?.Pos;
            if (deathPos != null)
                runningEvent.sapi.World.PlaySoundAt(new AssetLocation("game:sounds/effect/largeexplosion"),
                    deathPos.X, deathPos.Y, deathPos.Z, null, false, 250f);

            int remaining = runningEvent.ParticipantsStillInEvent.Count;
            if (remaining > 1)
                EventBroadcaster.Broadcast(runningEvent, Lang.Get("canevents:hg-remaining", remaining));

            // Keep the HUD's alive counter current; the zone packet carries it.
            if (showZoneParticles) SendZoneState(true);
        }

        // A mid-match reconnect spawns a fresh entity, leaving the damage and grace hooks on the old
        // one, so both must be re-attached.
        public override void OnParticipantJoinedGame()
        {
            foreach (var uid in runningEvent.ParticipantsBefore)
            {
                var pl = runningEvent.sapi.World.PlayerByUid(uid) as IServerPlayer;
                if (pl == null) continue;
                damageHooks.RehookIfStale(pl);
                graceHooks.RehookIfStale(pl);
            }
        }

        // Manager ticks behaviors at 1 Hz, so dt ≈ 1s, fine for damage cadence.
        public override void OnTick(float dt)
        {
            if (MatchResolved) return;

            float elapsed = runningEvent.ElapsedSeconds;

            // Release the pedestal hold once its window is over.
            if (freezeListenerId >= 0 && elapsed >= startFreezeSeconds)
            {
                EndStartFreeze();
                EventBroadcaster.Broadcast(runningEvent, Lang.Get("canevents:hg-freeze-end"));
            }

            // Grace ends on its own clock, independent of whether a zone is configured.
            if (graceActive && elapsed >= graceSeconds)
            {
                graceActive = false;
                graceHooks.UnhookAll();
                EventBroadcaster.Broadcast(runningEvent, Lang.Get("canevents:hg-grace-end"));
            }

            // Freeze window and truce run on separate clocks; bots fight once both are over.
            if (runningEvent.CombatSuspended && !graceActive && freezeListenerId < 0)
                runningEvent.CombatSuspended = false;

            // Sponsor gifts tick on their own clock, independent of the storm.
            if (sponsorEverySeconds > 0f)
            {
                if (nextSponsorAt == float.MaxValue) nextSponsorAt = sponsorEverySeconds;
                if (elapsed >= nextSponsorAt)
                {
                    nextSponsorAt = elapsed + sponsorEverySeconds;
                    SendSponsorGift();
                }
            }

            // The feast is independent of the storm being configured.
            if (feastAtSeconds > 0f)
            {
                if (!feastAnnounced && elapsed >= feastAtSeconds - feastAnnounceSeconds)
                {
                    feastAnnounced = true;
                    EventBroadcaster.Broadcast(runningEvent,
                        Lang.Get("canevents:hg-feast-soon", (int)Math.Max(1, feastAtSeconds - elapsed)));
                }
                if (!feastOpened && elapsed >= feastAtSeconds)
                {
                    feastOpened = true;
                    int refilled = loot.RefillTier(runningEvent, feastTier);
                    if (refilled > 0)
                        EventBroadcaster.Broadcast(runningEvent, Lang.Get("canevents:hg-feast-open", refilled));
                }
            }

            if (!zoneCenterSet || !zone.HasPhases) return;

            float radius = zone.RadiusAt(elapsed);
            float dps = zone.DpsAt(elapsed);

            AnnouncePhaseIfChanged(elapsed);
            WarnBeforeShrink(elapsed, radius);

            // Heartbeat only: the real sends happen on start and per phase change; the client
            // interpolates in between on its own.
            if (showZoneParticles && elapsed - lastZoneStateSentAt >= ZoneStateHeartbeatSeconds)
                SendZoneState(true);

            if (dps <= 0f) return;
            float radiusSq = radius * radius;
            float dmg = dps * dt;

            // BodyOf, not PlayerByUid: the storm must burn bots outside the ring too, or a bot that
            // never moves outlasts every tribute who played the zone properly.
            foreach (var uid in runningEvent.ParticipantsStillInEvent.ToArray())
            {
                var body = runningEvent.BodyOf(uid);
                if (body == null || !body.Alive) continue;
                var pos = body.Pos;
                double dx = pos.X - zoneCenterX;
                double dz = pos.Z - zoneCenterZ;
                if (dx * dx + dz * dz > radiusSq)
                {
                    body.ReceiveDamage(new DamageSource
                    {
                        Source = EnumDamageSource.Weather,
                        Type = EnumDamageType.Frost
                    }, dmg);
                }
            }
        }

        /// <summary>
        /// Sends the current shrink segment; clients interpolate the radius locally and draw the
        /// dome. Passing false clears it.
        /// </summary>
        private void SendZoneState(bool active)
        {
            lastZoneStateSentAt = runningEvent.ElapsedSeconds;

            var msg = new Network.HgZoneStateMsg
            {
                Active = active && zoneCenterSet && zone.HasPhases,
                AliveCount = runningEvent.ParticipantsStillInEvent.Count
            };
            if (msg.Active)
            {
                var (now, target, seconds) = zone.SegmentAt(runningEvent.ElapsedSeconds);
                msg.X = zoneCenterX;
                // zoneCenterY is the middle of the floor block; the dome stands on its surface.
                msg.Y = Math.Floor(zoneCenterY) + 1.0;
                msg.Z = zoneCenterZ;
                msg.RadiusNow = now;
                msg.RadiusTarget = target;
                msg.SecondsToTarget = seconds;
            }

            runningEvent.NetworkHandler?.SendHgZoneState(runningEvent, msg);
        }

        /// <summary>
        /// Heads-up 15s before the zone starts closing, sent once while the current segment is flat
        /// and a smaller phase is next. Mid-shrink boundaries stay silent: the phase announcement
        /// already covers those.
        /// </summary>
        private void WarnBeforeShrink(float elapsed, float radius)
        {
            int curIdx = zone.PhaseIndexAt(elapsed);
            int nextIdx = curIdx + 1;
            if (curIdx < 0 || nextIdx >= zone.PhaseCount || warnedPhaseIdx >= nextIdx) return;

            var (now, target, secondsToTarget) = zone.SegmentAt(elapsed);
            bool currentlyFlat = Math.Abs(target - now) < 0.5f;
            var (nextRadius, _) = zone.PhaseAt(nextIdx);
            if (!currentlyFlat || secondsToTarget > StormWarnSeconds || nextRadius >= radius - 0.5f) return;

            warnedPhaseIdx = nextIdx;
            EventBroadcaster.Broadcast(runningEvent, Lang.Get("canevents:hg-storm-warning", (int)secondsToTarget));
            foreach (var uid in runningEvent.ParticipantsStillInEvent)
            {
                var pl = runningEvent.sapi.World.PlayerByUid(uid) as IServerPlayer;
                var pos = pl?.Entity?.Pos;
                if (pos == null) continue;
                runningEvent.sapi.World.PlaySoundAt(new AssetLocation("game:sounds/effect/mediumexplosion"),
                    pos.X, pos.Y, pos.Z, null, false, 32f, 0.6f);
            }
        }

        /// <summary>
        /// Drops one rolled item above a random survivor, with a marker ring so nearby players can
        /// see and contest it.
        /// </summary>
        private void SendSponsorGift()
        {
            var alive = runningEvent.ParticipantsStillInEvent
                .Select(uid => runningEvent.sapi.World.PlayerByUid(uid) as IServerPlayer)
                .Where(p => p?.Entity != null && p.Entity.Alive)
                .ToList();
            if (alive.Count == 0) return;

            var target = alive[runningEvent.sapi.World.Rand.Next(alive.Count)];
            var stack = loot.RollItem(runningEvent, sponsorTier);
            if (stack == null) return;

            var pos = target.Entity.Pos;
            runningEvent.sapi.World.SpawnItemEntity(stack, new Vec3d(pos.X, pos.Y + 6, pos.Z));
            ZoneVisualizer.DrawRing(runningEvent.sapi, pos.X, pos.Y + 6, pos.Z, 1.2f, 10,
                ZoneVisualizer.OwnerColor(-2));
            EventBroadcaster.Broadcast(runningEvent,
                Lang.Get("canevents:hg-sponsor", target.PlayerName, stack.GetName()));
        }

        private void AnnouncePhaseIfChanged(float elapsed)
        {
            int idx = zone.PhaseIndexAt(elapsed);
            if (idx == announcedPhaseIdx) return;
            announcedPhaseIdx = idx;
            if (idx < 0) return;

            var (radius, dps) = zone.PhaseAt(idx);
            EventBroadcaster.Broadcast(runningEvent,
                Lang.Get("canevents:hg-storm-phase", idx + 1, zone.PhaseCount, radius, dps));

            // A phase boundary starts a new linear shrink segment, so give the clients its endpoint.
            if (showZoneParticles) SendZoneState(true);
        }

        /// <summary>
        /// Battle-royale placement: survival time decides, kills only break ties. Overrides
        /// KillScoringEB's kill-first default, where an early death with 3 kills would outplace the
        /// runner-up survivor.
        /// </summary>
        public override List<HashSet<string>> GetFinalRanking()
            => survival.Rank(runningEvent, uid => killCounts.TryGetValue(uid, out var k) ? k : 0);

        // Time-up: most kills wins, ties broken by damage dealt; still tied or no survivors -> draw.
        public override void OnBeforeFinish()
        {
            if (MatchResolved) return;

            // One group left is already covered by the default GetWinners(); latch it anyway so a
            // later call cannot overwrite the result.
            if (runningEvent.ParticipantsByGroupsStillInEvent.Count <= 1)
            {
                MarkMatchResolved();
                return;
            }

            string bestUid = null;
            int bestKills = -1;
            float bestDmg = -1f;
            bool tied = false;
            foreach (var uid in runningEvent.ParticipantsByGroupsStillInEvent.SelectMany(g => g))
            {
                int k = killCounts.TryGetValue(uid, out var c) ? c : 0;
                float d = damageDealt.TryGetValue(uid, out var dd) ? dd : 0f;
                if (k > bestKills || (k == bestKills && d > bestDmg))
                {
                    bestUid = uid; bestKills = k; bestDmg = d; tied = false;
                }
                else if (k == bestKills && d == bestDmg)
                {
                    tied = true; // equal on both keys with the current leader
                }
            }

            // The whole group containing bestUid wins (covers team mode); null = draw.
            var winnerGroup = bestUid != null && !tied
                ? runningEvent.ParticipantsByGroupsStillInEvent.FirstOrDefault(g => g.Contains(bestUid))
                : null;
            // The manager is already finishing us (this IS the time-up path).
            ResolveMatch(winnerGroup, "hunger games time-up", requestFinish: false);
        }

        public override void OnEnd()
        {
            // First, and in its own guard: this is the only step here that writes to the world, and a
            // throw from any of the teardown below it used to leave the chests standing for good.
            log.Safely("loot restore", () => loot.Restore(runningEvent));

            graceHooks.UnhookAll();
            damageHooks.UnhookAll();
            EndStartFreeze();   // safety: a match cancelled mid-countdown must not leak the listener
            if (blockEditsHooked)
            {
                blockEditsHooked = false;
                runningEvent.sapi.Event.CanPlaceOrBreakBlock -= OnCanPlaceOrBreak;
            }
            // Clear the dome from every client, or the last radius stays painted forever.
            SendZoneState(false);
            EventBroadcaster.Broadcast(runningEvent, Lang.Get("canevents:hg-ended"));
        }
    }
}
