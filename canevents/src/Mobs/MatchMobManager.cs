using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.Bots;
using canevents.src.EventManagers;
using canevents.src.EventPlacesManagers;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace canevents.src.Mobs
{
    // Mobs of one match plus its director: once a second it hands every mob a target, so the pack
    // spreads over the fighters instead of piling onto the nearest one. The AI patch then only has
    // to compare entity ids.
    public class MatchMobManager
    {
        private const int CorpseLingerMs = 4000;
        private const double CloseRangeSq = 3 * 3;
        private const double DropTargetRangeSq = 40 * 40;
        private const float StuckSeconds = 8f;
        private const double StuckMoveSq = 1.0;
        private const double RallyWhenFartherSq = 12 * 12;
        private const double KeepTargetMargin = 4.0;
        private const double ThreatWeight = 1.5;
        private const double WoundedWeight = 6.0;
        private const float ThreatDecay = 0.85f;
        private const double GuardTriggerSq = 6 * 6;
        private const double GuardReachSq = 20 * 20;
        private const int GuardsPerArcher = 2;
        private const double MaxSlotSpacing = 1.1;

        private static readonly Dictionary<long, MatchMobManager> byMatch = new Dictionary<long, MatchMobManager>();

        public static MatchMobManager Of(long matchId)
            => byMatch.TryGetValue(matchId, out var manager) ? manager : null;

        private sealed class Tracked
        {
            public Entity Body;
            public BlockPos Home;
            public Vec3d LastPos;
            public float StillFor;
            public long TargetId;
            public Entity Target;
            // Where around its target to come in from: radians off the target's back. NaN = straight at it.
            public float Approach = float.NaN;
            public bool Guarding;
            // Damage taken per attacker entity id, decays every director tick.
            public readonly Dictionary<long, float> Threat = new Dictionary<long, float>();
        }

        private readonly RunningEvent match;
        private readonly Log log;
        private readonly Dictionary<long, Tracked> live = new Dictionary<long, Tracked>();
        private readonly List<BlockPos> rallyPoints = new List<BlockPos>();
        private long tickId = -1;

        // Extra distance (blocks) a fighter "costs" per mob already on them. 0 = every mob takes the nearest.
        public float SpreadPenalty { get; set; } = 4f;

        public MatchMobManager(RunningEvent match)
        {
            this.match = match;
            log = Log.For(match?.sapi?.World?.Logger, "mobs");
            byMatch[match.Id] = this;
        }

        public int AliveCount => live.Count;

        public IEnumerable<Entity> Alive => live.Values.Select(t => t.Body);

        // Where a stuck mob is put back, nearest to its target. Its own spawn point when empty.
        public void SetRallyPoints(IEnumerable<BlockPos> points)
        {
            rallyPoints.Clear();
            if (points != null) rallyPoints.AddRange(points.Where(p => p != null));
        }

        public static string Unavailable(ICoreServerAPI sapi, MobEntry entry)
        {
            var provider = MobProviders.For(entry);
            return provider == null ? "no installed mod provides it" : provider.Unavailable(sapi, entry);
        }

        // Waits for the chunk like ArenaBotManager.Spawn. False when nothing can provide the mob.
        public bool Spawn(MobEntry entry, BlockPos pos, float healthMul, float damageMul, Action<Entity> onSpawned = null)
        {
            var sapi = match.sapi;
            var provider = MobProviders.For(entry);
            if (provider == null || pos == null) return false;
            MobLines.ApplyMutators(match.Event, sapi.World.Rand, ref healthMul, ref damageMul);

            if (DimensionChunks.IsLoaded(sapi, pos))
            {
                var bodies = SpawnNow(provider, entry, pos, healthMul, damageMul);
                foreach (var body in bodies) onSpawned?.Invoke(body);
                return bodies.Count > 0;
            }

            int cx = DimensionChunks.ColumnOf(pos.X);
            int cz = DimensionChunks.ColumnOf(pos.Z);
            PendingCount++;
            DimensionChunks.LoadColumns(sapi, new[] { (cx, cz) }, pos.dimension,
                () =>
                {
                    PendingCount = Math.Max(0, PendingCount - 1);
                    if (match.EventState != EventState.Running) return;
                    foreach (var body in SpawnNow(provider, entry, pos, healthMul, damageMul)) onSpawned?.Invoke(body);
                },
                () =>
                {
                    PendingCount = Math.Max(0, PendingCount - 1);
                    log.Warning("Column {0}/{1} of dimension {2} did not load; {3} is not spawned.",
                        cx, cz, pos.dimension, entry.Describe());
                });
            return true;
        }

        // Spawns waiting for a chunk; a wave isn't over while any are on the way.
        public int PendingCount { get; private set; }

        private List<Entity> SpawnNow(IMobProvider provider, MobEntry entry, BlockPos pos, float healthMul, float damageMul)
        {
            List<Entity> bodies;
            try
            {
                bodies = provider.Spawn(match.sapi, entry, pos, e =>
                {
                    MatchMobLink.Tag(e, match.Id, damageMul);
                    e.AlwaysActive = true;
                }) ?? new List<Entity>();
            }
            catch (Exception e)
            {
                log.Error("{0} via {1} failed to spawn: {2}", entry.Describe(), provider.Id, e);
                return new List<Entity>();
            }
            if (bodies.Count == 0) log.Warning("{0} via {1} was not spawned.", entry.Describe(), provider.Id);

            foreach (var body in bodies)
            {
                Adopt(body, pos, damageMul, healthMul);
                Ai.RaiderProfiles.Equip(match.sapi, body, entry);
            }
            return bodies;
        }

        // Also for bodies a mob brought in itself (canrpgmobs summons), so they count, fight and get cleaned up.
        public void Adopt(Entity body, BlockPos home, float damageMul, float healthMul = 1f)
        {
            if (body == null || live.ContainsKey(body.EntityId)) return;

            // A provider that spawns first ran beforeSpawn after the fact, which is fine for WatchedAttributes.
            if (!MatchMobLink.IsMatchMob(body)) MatchMobLink.Tag(body, match.Id, damageMul);
            Hook(body);

            var health = body.GetBehavior<EntityBehaviorHealth>();
            if (health != null && healthMul > 0f && Math.Abs(healthMul - 1f) > 0.001f)
            {
                health.BaseMaxHealth *= healthMul;
                health.Health = health.MaxHealth;
                health.MarkDirty();
            }

            live[body.EntityId] = new Tracked { Body = body, Home = (home ?? body.Pos.AsBlockPos).Copy(), LastPos = body.Pos.XYZ };
            EnsureTick();
        }

        private void Hook(Entity body)
        {
            if (body is EntityAgent agent) agent.AllowDespawn = false;
            body.AlwaysActive = true;
            body.State = EnumEntityState.Active;

            // Vanilla drifters expire with no player around; a mob that vanishes would silently shorten the wave.
            // No loot either: an arena must not become a farm for temporal gears and hides.
            foreach (var behavior in body.SidedProperties?.Behaviors?
                         .Where(b => b is EntityBehaviorDespawn || b is EntityBehaviorHarvestable).ToList()
                     ?? new List<EntityBehavior>())
                body.RemoveBehavior(behavior);
            if (body.Properties != null) body.Properties.Drops = null;

            var taskAi = body.GetBehavior<EntityBehaviorTaskAI>();
            if (taskAi != null) taskAi.TaskManager.OnShouldExecuteTask += _ => !match.MovementFrozen;

            var health = body.GetBehavior<EntityBehaviorHealth>();
            long id = body.EntityId;
            if (health != null) health.onDamaged += (damage, source) =>
            {
                if (!AcceptsDamage(source)) return 0f;
                NoteThreat(id, source, damage);
                return damage;
            };
        }

        private void NoteThreat(long mobId, DamageSource source, float damage)
        {
            var cause = source?.GetCauseEntity();
            if (cause == null || damage <= 0f || !live.TryGetValue(mobId, out var tracked)) return;
            tracked.Threat.TryGetValue(cause.EntityId, out float before);
            tracked.Threat[cause.EntityId] = before + damage;
        }

        // A chunk reload mid-match brings the mob back as a new object under the same id.
        public bool Readopt(Entity mob)
        {
            if (mob == null || !live.TryGetValue(mob.EntityId, out var tracked)) return false;
            if (ReferenceEquals(tracked.Body, mob)) return true;
            tracked.Body = mob;
            Hook(mob);
            return true;
        }

        // Hit by its own match's fighters and the world (falls, fire), nobody else. No infighting.
        private bool AcceptsDamage(DamageSource source)
        {
            var cause = source?.GetCauseEntity();
            if (cause == null) return true;
            if (MatchMobLink.IsMatchMob(cause)) return false;
            string uid = BotMatchLink.UidOf(cause);
            return uid != null && match.IsActive(uid) && !match.CombatSuspended;
        }

        public bool MayTarget(Entity mob, Entity target)
        {
            if (match.EventState != EventState.Running || match.CombatSuspended) return false;
            string uid = BotMatchLink.UidOf(target);
            if (uid == null || !match.IsActive(uid)) return false;

            if (live.TryGetValue(mob.EntityId, out var tracked) && tracked.TargetId == target.EntityId) return true;
            return SquareDistance(mob, target) <= CloseRangeSq;
        }

        // From CANEventManager.OnEntityDeath. Listeners run before the body is forgotten.
        public void OnMobDied(Entity mob, DamageSource source)
        {
            if (!live.Remove(mob.EntityId)) return;

            foreach (var listener in match.Behaviors.OfType<IMatchMobListener>())
            {
                var l = listener;
                log.Safely("OnMobDeath", () => l.OnMobDeath(mob, source));
            }

            var deathSource = source;
            match.sapi.Event.RegisterCallback(_ =>
            {
                if (mob.World != null)
                    match.sapi.World.DespawnEntity(mob,
                        new EntityDespawnData { Reason = EnumDespawnReason.Death, DamageSourceForDeath = deathSource });
            }, CorpseLingerMs);
        }

        private void EnsureTick()
        {
            if (tickId >= 0) return;
            tickId = match.RegisterMatchTick(dt => log.Safely("director tick", () => Tick(dt)), 1000);
        }

        private void Tick(float dt)
        {
            Prune();
            if (live.Count == 0) return;

            var fighters = match.LiveBodies().Select(b => b.body).ToList();
            AssignTargets(fighters);

            foreach (var tracked in live.Values) KeepInPlay(tracked, fighters, dt);
        }

        // Despawned by someone else (admin kill, unloaded chunk).
        private void Prune()
        {
            foreach (var id in live.Where(p => p.Value.Body.World == null || !p.Value.Body.Alive).Select(p => p.Key).ToList())
                live.Remove(id);
        }

        // Our own AI tasks read this instead of scanning; vanilla ones go through MayTarget.
        public Entity TargetOf(Entity mob)
        {
            if (mob == null || match.CombatSuspended || !live.TryGetValue(mob.EntityId, out var tracked)) return null;
            var target = tracked.Target;
            if (target == null || !target.Alive || target.World == null) return null;
            string uid = BotMatchLink.UidOf(target);
            return uid != null && match.IsActive(uid) ? target : null;
        }

        // Cost in blocks: distance, plus SpreadPenalty per mob already on that fighter, minus whoever hurt
        // this mob, minus how wounded the fighter is. A mob keeps its target unless another is clearly cheaper.
        private void AssignTargets(List<Entity> fighters)
        {
            foreach (var tracked in live.Values)
                foreach (var key in tracked.Threat.Keys.ToList())
                {
                    float left = tracked.Threat[key] * ThreatDecay;
                    if (left < 0.1f) tracked.Threat.Remove(key);
                    else tracked.Threat[key] = left;
                }

            if (fighters.Count == 0)
            {
                foreach (var tracked in live.Values)
                {
                    tracked.TargetId = 0;
                    tracked.Target = null;
                }
                return;
            }

            var load = fighters.ToDictionary(f => f.EntityId, _ => 0);
            var wounded = fighters.ToDictionary(f => f.EntityId, f => WoundOf(f));

            foreach (var tracked in live.Values) tracked.Guarding = false;
            AssignGuards(fighters, load);

            foreach (var tracked in live.Values)
            {
                if (tracked.Guarding) continue;
                Entity best = null, current = null;
                double bestCost = double.MaxValue, currentCost = double.MaxValue;
                foreach (var fighter in fighters)
                {
                    double distSq = SquareDistance(tracked.Body, fighter);
                    tracked.Threat.TryGetValue(fighter.EntityId, out float threat);
                    double cost = Math.Sqrt(distSq) + load[fighter.EntityId] * SpreadPenalty
                                  - threat * ThreatWeight - wounded[fighter.EntityId] * WoundedWeight;
                    if (fighter.EntityId == tracked.TargetId && distSq <= DropTargetRangeSq)
                    {
                        current = fighter;
                        currentCost = cost;
                    }
                    if (cost >= bestCost) continue;
                    bestCost = cost;
                    best = fighter;
                }

                var pick = current != null && currentCost <= bestCost + KeepTargetMargin ? current : best;
                tracked.TargetId = pick.EntityId;
                tracked.Target = pick;
                load[pick.EntityId]++;
            }

            AssignApproaches();
        }

        // A fighter that gets close to one of our archers is taken on by the nearest melee mobs.
        private void AssignGuards(List<Entity> fighters, Dictionary<long, int> load)
        {
            var archers = live.Values.Where(t => Ai.RaiderProfiles.Of(t.Body).Ranged && Ai.RaiderProfiles.IsRaider(t.Body)).ToList();
            if (archers.Count == 0) return;

            foreach (var archer in archers)
                foreach (var fighter in fighters)
                {
                    if (SquareDistance(archer.Body, fighter) > GuardTriggerSq) continue;
                    var guards = live.Values
                        .Where(t => !t.Guarding && !Ai.RaiderProfiles.Of(t.Body).Ranged
                                    && SquareDistance(t.Body, archer.Body) <= GuardReachSq)
                        .OrderBy(t => SquareDistance(t.Body, fighter))
                        .Take(GuardsPerArcher);
                    foreach (var guard in guards)
                    {
                        guard.Guarding = true;
                        guard.TargetId = fighter.EntityId;
                        guard.Target = fighter;
                        load[fighter.EntityId]++;
                    }
                }
        }

        // Mobs on the same target get places around it, centred on its back, in the order they already
        // stand so their paths don't cross. Nobody walks in a column or comes from one side.
        private void AssignApproaches()
        {
            foreach (var group in live.Values.Where(t => t.Target != null).GroupBy(t => t.TargetId))
            {
                var target = group.First().Target;
                var back = target.Pos.GetViewVector();
                double backAngle = Math.Atan2(-back.X, -back.Z);

                var ordered = group
                    .OrderBy(t => GameMath.AngleRadDistance((float)backAngle,
                        (float)Math.Atan2(t.Body.Pos.X - target.Pos.X, t.Body.Pos.Z - target.Pos.Z)))
                    .ToList();
                int n = ordered.Count;
                double spacing = Math.Min(GameMath.TWOPI / n, MaxSlotSpacing);
                for (int i = 0; i < n; i++) ordered[i].Approach = (float)((i - (n - 1) / 2.0) * spacing);
            }
        }

        // Radians off the target's back this mob should come in from, NaN if it has no target.
        public float ApproachOf(Entity mob)
            => mob != null && live.TryGetValue(mob.EntityId, out var tracked) && tracked.Target != null ? tracked.Approach : float.NaN;

        private static float WoundOf(Entity fighter)
        {
            var health = fighter.GetBehavior<EntityBehaviorHealth>();
            return health == null || health.MaxHealth <= 0f ? 0f : GameMath.Clamp(1f - health.Health / health.MaxHealth, 0f, 1f);
        }

        // Out of the arena, or standing still far from its target: back to the rally point nearest the target.
        private void KeepInPlay(Tracked tracked, List<Entity> fighters, float dt)
        {
            var body = tracked.Body;
            var pos = body.Pos;
            double moved = pos.SquareDistanceTo(tracked.LastPos);
            tracked.LastPos.Set(pos.X, pos.Y, pos.Z);
            tracked.StillFor = moved < StuckMoveSq ? tracked.StillFor + dt : 0f;

            var target = fighters.FirstOrDefault(f => f.EntityId == tracked.TargetId);
            bool outside = !InsideArena(pos.AsBlockPos);
            bool stuck = tracked.StillFor >= StuckSeconds && target != null && SquareDistance(body, target) > RallyWhenFartherSq;
            if (!outside && !stuck) return;

            var to = RallyPointNear(target) ?? tracked.Home;
            body.TeleportToDouble(to.X + 0.5, to.Y, to.Z + 0.5);
            tracked.StillFor = 0f;
            log.Debug("{0} id={1} {2}, moved to {3}", body.GetName(), body.EntityId, outside ? "left the arena" : "was stuck", to);
        }

        private bool InsideArena(BlockPos pos)
        {
            var place = match.Place;
            if (place == null || !place.HasBounds || place.BoundsMin == null || place.BoundsMax == null) return true;
            const int slack = 2;
            return pos.X >= place.BoundsMin.X - slack && pos.X <= place.BoundsMax.X + slack
                && pos.Y >= place.BoundsMin.Y - slack && pos.Y <= place.BoundsMax.Y + slack
                && pos.Z >= place.BoundsMin.Z - slack && pos.Z <= place.BoundsMax.Z + slack;
        }

        private BlockPos RallyPointNear(Entity target)
        {
            if (rallyPoints.Count == 0) return null;
            if (target == null) return rallyPoints[match.sapi.World.Rand.Next(rallyPoints.Count)];
            return rallyPoints.OrderBy(p => target.Pos.SquareDistanceTo(p.X + 0.5, p.Y, p.Z + 0.5)).First();
        }

        private static double SquareDistance(Entity a, Entity b)
        {
            double dx = a.Pos.X - b.Pos.X, dy = a.Pos.Y - b.Pos.Y, dz = a.Pos.Z - b.Pos.Z;
            return dx * dx + dy * dy + dz * dz;
        }

        // Mid-match clear (a boss fell): the director keeps running for the next wave.
        public void DespawnRest()
        {
            foreach (var tracked in live.Values.ToList())
                if (tracked.Body?.World != null)
                    match.sapi.World.DespawnEntity(tracked.Body, new EntityDespawnData { Reason = EnumDespawnReason.Removed });
            live.Clear();
        }

        // Every match end, aborts included. AllowDespawn is off, so Die() would leave the corpse forever.
        public void DespawnAll()
        {
            foreach (var tracked in live.Values)
            {
                if (tracked.Body?.World == null) continue;
                match.sapi.World.DespawnEntity(tracked.Body, new EntityDespawnData { Reason = EnumDespawnReason.Removed });
            }
            live.Clear();
            if (tickId >= 0) match.UnregisterMatchTick(tickId);
            tickId = -1;
            byMatch.Remove(match.Id);
        }
    }
}
