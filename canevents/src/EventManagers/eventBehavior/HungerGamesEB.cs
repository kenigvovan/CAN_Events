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
    // One-life elimination with a shrinking storm. Zone centre/radius come from the place, phases from event attributes.
    // IDrawsOwnBoundary: the storm ring is the edge, so the map footprint is not outlined.
    public class HungerGamesEB : EliminatingKillScoringEB, IStartCryProvider, IDrawsOwnBoundary
    {

        private readonly ShrinkingZone zone = new ShrinkingZone();
        private double zoneCenterX, zoneCenterY, zoneCenterZ;
        private bool zoneCenterSet = false;
        private int announcedPhaseIdx = -1;

        private readonly HungerGamesLoot loot = new HungerGamesLoot();

        // Dome is drawn client-side (HgZoneDomeRenderer); server sends segments on start, per phase and as heartbeat.
        private bool showZoneParticles = true;

        // Heartbeat so a client that reconnected mid-match gets the current segment.
        private const float ZoneStateHeartbeatSeconds = 15f;
        private float lastZoneStateSentAt = float.MinValue;

        // Opening PvP truce; storm and falls still apply. 0 disables.
        private float graceSeconds = 0f;
        private bool graceActive = false;
        private readonly DamageHookSet graceHooks = new DamageHookSet();

        // Time-up tiebreak after kills.
        private readonly Dictionary<string, float> damageDealt = new Dictionary<string, float>();
        private readonly DamageHookSet damageHooks = new DamageHookSet();

        // No block place/break for live tributes during the match.
        private readonly ArenaBlockGuard blockGuard = new ArenaBlockGuard();

        // Pedestal hold for the first startFreezeSeconds; strays are snapped back.
        private float startFreezeSeconds = 5f;
        private MovementHold startHold;

        // Feast: feastTier chests re-rolled at feastAtSeconds. 0 disables.
        private float feastAtSeconds = 0f;
        private float feastAnnounceSeconds = 30f;
        private string feastTier = null;
        private bool feastAnnounced, feastOpened;

        // Random sponsorTier item dropped on a survivor every N seconds. 0 disables; keep 0 for ranked.
        private float sponsorEverySeconds = 0f;
        private string sponsorTier = null;
        private float nextSponsorAt = float.MaxValue;

        private const float StormWarnSeconds = 15f;
        private int warnedPhaseIdx = -1;

        // Death order, used for placement.
        private readonly SurvivalRanking survival = new SurvivalRanking();

        public HungerGamesEB(string name, JsonObject attributes, RunningEvent runningEvent)
            : base(name, attributes, runningEvent) { }

        public override string Name => BehaviorNames.HungerGames;

        public string StartCryLangKey => "canevents:hungergames-start-cry";

        protected override float DefaultTimeLimitSeconds => BehaviorConfigKeys.HungerGamesTimeLimitSeconds;

        protected override void ReadModeConfig()
        {
            showZoneParticles = Config.Bool(BehaviorConfigKeys.ShowZoneParticles, true);
            graceSeconds = Config.Float("gracePeriodSeconds", 0f);
            startFreezeSeconds = Config.Float("startFreezeSeconds", 5f);
            feastAtSeconds = Config.Float("feastAtSeconds", 0f);
            feastAnnounceSeconds = Config.Float("feastAnnounceSeconds", 30f);
            feastTier = Config.String("feastTier");
            sponsorEverySeconds = Config.Float("sponsorEverySeconds", 0f);
            sponsorTier = Config.String("sponsorTier");
            zone.ParsePhases(Attributes);
            loot.Parse(Attributes);

            // Parsed elsewhere; declared so the unknown-attribute check doesn't flag them.
            Config.Has(ShrinkingZone.PhasesKey);
            Config.Has("lootContainerBlock");
            Config.Has("defaultLootTier");
            Config.Has("minSlotsPerChest");
            Config.Has("maxSlotsPerChest");
            Config.Has("lootTables");
        }

        public override void OnBeforeStart()
        {
            BeginMatch();
            survival.Clear();
            damageDealt.Clear();

            ResolveZoneFromPlace();
            loot.Place(runningEvent);

            if (!blockGuard.Active)
                blockGuard.DenyEditsByActive(runningEvent, BehaviorNames.HungerGamesClaimant,
                    "canevents-hg-nobuild", "canevents:hg-no-build");
        }

        // Pedestal hold: the shared MovementHold puts back anyone who steps off.
        private void BeginStartFreeze()
        {
            runningEvent.CombatSuspended = true;
            startHold ??= new MovementHold(runningEvent, "canevents:hg-hold-error");
            startHold.Begin();
            runningEvent.BeginHold(startFreezeSeconds, "canevents:hg-hold-hud");
            // Players on adjacent pedestals could still trade hits.
            HookGrace();
            Announce("canevents:hg-freeze");
        }

        private bool TruceActive => graceActive || startHold?.Active == true;

        private void EndStartFreeze()
        {
            startHold?.End();
            runningEvent.EndHold();
        }

        private void ResolveZoneFromPlace()
        {
            var place = runningEvent.Place;

            if (place != null && place.TryGetBehaviorData(BehaviorKeys.HungerGames, out var data)
                && data["InitialRadius"] != null)
            {
                zone.InitialRadius = data.Value<float>("InitialRadius");
            }

            var origin = PlaceZone.TryResolveCentre(place, BehaviorKeys.HungerGames,
                out zoneCenterX, out zoneCenterY, out zoneCenterZ);
            zoneCenterSet = origin != ZoneOrigin.None;

            if (origin == ZoneOrigin.BoundsCentre)
            {
                runningEvent.sapi.World.Logger.Notification(
                    "[HG] Place id={0} has no HungerGames zone center configured; using bounds center.", place.Id);
            }
            else if (origin == ZoneOrigin.None)
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

            // Without a zone, clients still need Active=false to clear a leftover dome.
            if (zoneCenterSet && zone.HasPhases) AnnouncePhaseIfChanged(0f);
            else if (showZoneParticles) SendZoneState(true);
        }

        // Damage tally for the tiebreak, bots included. Re-called from OnTick for late bot bodies.
        private void HookDamageTracking()
            => damageHooks.HookAll(runningEvent, runningEvent.ParticipantsBefore, TrackDamage, includeBots: true);

        private float TrackDamage(string victimUid, float damage, DamageSource source)
        {
            if (TruceActive) return damage;
            if (DamageHookSet.TryHostileHit(runningEvent, victimUid, damage, source, out var au, includeBots: true))
                damageDealt[au] = DamageDealtBy(au) + damage;
            return damage;
        }

        private float DamageDealtBy(string uid) => damageDealt.TryGetValue(uid, out var d) ? d : 0f;

        // Hook checks TruceActive itself, so a late teardown can't leave players immortal.
        private void BeginGrace()
        {
            graceActive = true;
            runningEvent.CombatSuspended = true;
            HookGrace();
            Announce("canevents:hg-grace-start", (int)graceSeconds);
        }

        private void HookGrace()
            => graceHooks.HookAll(runningEvent, runningEvent.ParticipantsBefore, GraceDamage, includeBots: true);

        private float GraceDamage(string victimUid, float damage, DamageSource source)
        {
            if (!TruceActive || damage <= 0f) return damage;
            // Self-inflicted included.
            return DamageHookSet.TryParticipantAttacker(runningEvent, source, out _, includeBots: true) ? 0f : damage;
        }

        protected override string KillFeedVerb => "eliminated";

        protected override void OnScoredDeath(string victimUid, Entity victimBody, KillInfo kill)
        {
            survival.Add(victimUid);
            Announce("canevents:hg-player-eliminated", runningEvent.NameOf(victimUid), kill.KillerName);
        }

        // No MakeSpectator here: CANEventManager.OnDeath does the handoff.
        protected override void AfterEliminated(string victimUid, Entity victimBody, KillInfo kill)
        {
            var deathPos = victimBody?.Pos;
            if (deathPos != null)
                runningEvent.sapi.World.PlaySoundAt(new AssetLocation("game:sounds/effect/largeexplosion"),
                    deathPos.X, deathPos.Y, deathPos.Z, null, false, 250f);

            int remaining = runningEvent.ParticipantsStillInEvent.Count;
            if (remaining > 1)
                Announce("canevents:hg-remaining", remaining);

            // The zone packet carries the HUD alive counter.
            if (showZoneParticles) SendZoneState(true);
        }

        // Reconnect spawns a fresh entity; hooks are still on the old one.
        public override void OnParticipantJoinedGame()
        {
            damageHooks.RehookAll(runningEvent);
            graceHooks.RehookAll(runningEvent);
        }

        public override bool WantsTick => true;

        // Ticked at 1 Hz by the manager.
        public override void OnTick(float dt)
        {
            if (MatchResolved) return;

            float elapsed = runningEvent.ElapsedSeconds;

            if (startHold?.Active == true && elapsed >= startFreezeSeconds)
            {
                EndStartFreeze();
                Announce("canevents:hg-freeze-end");
            }

            if (graceActive && elapsed >= graceSeconds)
            {
                graceActive = false;
                Announce("canevents:hg-grace-end");
            }

            // Bot bodies can appear after OnStart (chunk loading); no-op once hooked.
            HookDamageTracking();
            if (TruceActive) HookGrace();
            else graceHooks.UnhookAll();

            if (runningEvent.CombatSuspended && !TruceActive)
                runningEvent.CombatSuspended = false;

            if (sponsorEverySeconds > 0f)
            {
                if (nextSponsorAt == float.MaxValue) nextSponsorAt = sponsorEverySeconds;
                if (elapsed >= nextSponsorAt)
                {
                    nextSponsorAt = elapsed + sponsorEverySeconds;
                    SendSponsorGift();
                }
            }

            if (feastAtSeconds > 0f)
            {
                if (!feastAnnounced && elapsed >= feastAtSeconds - feastAnnounceSeconds)
                {
                    feastAnnounced = true;
                    Announce("canevents:hg-feast-soon", (int)Math.Max(1, feastAtSeconds - elapsed));
                }
                if (!feastOpened && elapsed >= feastAtSeconds)
                {
                    feastOpened = true;
                    int refilled = loot.RefillTier(runningEvent, feastTier);
                    if (refilled > 0)
                        Announce("canevents:hg-feast-open", refilled);
                }
            }

            if (!zoneCenterSet || !zone.HasPhases) return;

            float radius = zone.RadiusAt(elapsed);
            float dps = zone.DpsAt(elapsed);

            AnnouncePhaseIfChanged(elapsed);
            WarnBeforeShrink(elapsed, radius);

            if (showZoneParticles && elapsed - lastZoneStateSentAt >= ZoneStateHeartbeatSeconds)
                SendZoneState(true);

            if (dps <= 0f) return;
            float dmg = dps * dt;

            // Bodies, not players: the storm burns bots too.
            foreach (var (_, body) in runningEvent.LiveBodies())
            {
                if (!CylinderZone.WithinRadius(body.Pos, zoneCenterX, zoneCenterZ, radius))
                {
                    body.ReceiveDamage(new DamageSource
                    {
                        Source = EnumDamageSource.Weather,
                        Type = EnumDamageType.Frost
                    }, dmg);
                }
            }
        }

        // Clients interpolate the radius within the segment. false clears the dome.
        private void SendZoneState(bool active)
        {
            lastZoneStateSentAt = runningEvent.ElapsedSeconds;

            int alive = runningEvent.ParticipantsStillInEvent.Count;
            if (!(active && zoneCenterSet && zone.HasPhases))
            {
                EventBroadcaster.ZoneState(runningEvent, false, alive);
                return;
            }

            var (now, target, seconds) = zone.SegmentAt(runningEvent.ElapsedSeconds);
            // zoneCenterY is the middle of the floor block; the dome stands on its surface.
            EventBroadcaster.ZoneState(runningEvent, true, alive,
                zoneCenterX, Math.Floor(zoneCenterY) + 1.0, zoneCenterZ, now, target, seconds);
        }

        // Only warns while the zone is flat and a smaller phase is next.
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
            Announce("canevents:hg-storm-warning", (int)secondsToTarget);
            foreach (var uid in runningEvent.ParticipantsStillInEvent)
            {
                var pl = runningEvent.sapi.World.PlayerByUid(uid) as IServerPlayer;
                var pos = pl?.Entity?.Pos;
                if (pos == null) continue;
                runningEvent.sapi.World.PlaySoundAt(new AssetLocation("game:sounds/effect/mediumexplosion"),
                    pos.X, pos.Y, pos.Z, null, false, 32f, 0.6f);
            }
        }

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
                ZoneVisualizer.SponsorMarkerColor);
            Announce("canevents:hg-sponsor", target.PlayerName, stack.GetName());
        }

        private void AnnouncePhaseIfChanged(float elapsed)
        {
            int idx = zone.PhaseIndexAt(elapsed);
            if (idx == announcedPhaseIdx) return;
            announcedPhaseIdx = idx;
            if (idx < 0) return;

            var (radius, dps) = zone.PhaseAt(idx);
            Announce("canevents:hg-storm-phase", idx + 1, zone.PhaseCount, radius, dps);

            if (showZoneParticles) SendZoneState(true);
        }

        // Placement by survival time, kills only break ties.
        public override List<HashSet<string>> GetFinalRanking()
            => survival.Rank(runningEvent, KillsOf);

        // Time-up: most kills, then damage dealt; still tied -> draw.
        public override void OnBeforeFinish()
        {
            if (MatchResolved) return;

            // Latch so a later call cannot overwrite the result.
            if (runningEvent.ParticipantsByGroupsStillInEvent.Count <= 1)
            {
                MarkMatchResolved();
                return;
            }

            bool decided = ScoringUtil.TryArgMax(
                runningEvent.ParticipantsByGroupsStillInEvent.SelectMany(g => g),
                uid => (KillsOf(uid), DamageDealtBy(uid)),
                out string bestUid);

            // null = draw.
            var winnerGroup = decided
                ? runningEvent.ParticipantsByGroupsStillInEvent.FirstOrDefault(g => g.Contains(bestUid))
                : null;
            // Already on the manager's finish path.
            ResolveMatch(winnerGroup, "hunger games time-up", requestFinish: false);
        }

        public override void OnEnd()
        {
            // First and guarded: a throw in later teardown used to leave the chests in the world.
            log.Safely("loot restore", () => loot.Restore(runningEvent));

            graceHooks.UnhookAll();
            damageHooks.UnhookAll();
            EndStartFreeze();   // match may be cancelled mid-hold
            blockGuard.Release();
            SendZoneState(false);
            Announce("canevents:hg-ended");
        }
    }
}
