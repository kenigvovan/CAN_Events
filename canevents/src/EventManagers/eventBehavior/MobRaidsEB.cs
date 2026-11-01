using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers.eventBehavior.Shared;
using canevents.src.EventManagers.events;
using canevents.src.Mobs;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;

namespace canevents.src.EventManagers.eventBehavior
{
    // Mob raids on top of any mode (Hunger Games, King of the Hill, deathmatch): every so often a band of
    // match mobs comes in and attacks whoever is still fighting, all sides alike. The mode's own rules
    // decide the match; a death to a mob is just a death with no killer credited.
    //   firstRaidSeconds (60), intervalSeconds (90), spawnIntervalSeconds (0.6), maxAliveMobs (16)
    //   healthPerRaid / damagePerRaid (0.1)  growth per raid
    //   raids: [{ name?, mobs: [MobEntry] }]  played in order, the last one repeats
    // Spawn points: /caneventplace mobspawn [tag]; for King of the Hill put them around the hill.
    public class MobRaidsEB : CANEventBehavior, IMatchMobListener
    {
        private sealed class Raid
        {
            public string Name;
            public List<MobEntry> Mobs;
        }

        private readonly List<Raid> configured = new List<Raid>();
        private List<Raid> raids = new List<Raid>();
        private readonly MobSpawnPoints spawnPoints = new MobSpawnPoints();
        private readonly Queue<MobEntry> queue = new Queue<MobEntry>();
        private readonly DamageHookSet hooks = new DamageHookSet();

        private float firstRaidSeconds = 60f;
        private float intervalSeconds = 90f;
        private float spawnIntervalSeconds = 0.6f;
        private int maxAliveMobs = 16;
        private float healthPerRaid = 0.1f;
        private float damagePerRaid = 0.1f;

        private int raidIndex = -1;
        private float nextRaidAt;
        private float nextSpawnAt;

        public MobRaidsEB(string name, JsonObject attributes, RunningEvent runningEvent)
            : base(name, attributes, runningEvent) { }

        public override string Name => BehaviorNames.MobRaids;

        public override bool WantsTick => true;

        public override void Initialize()
        {
            firstRaidSeconds = Math.Max(5f, Config.Float("firstRaidSeconds", 60f));
            intervalSeconds = Math.Max(10f, Config.Float("intervalSeconds", 90f));
            spawnIntervalSeconds = Math.Max(0f, Config.Float("spawnIntervalSeconds", 0.6f));
            maxAliveMobs = Math.Max(1, Config.Int("maxAliveMobs", 16));
            healthPerRaid = Math.Max(0f, Config.Float("healthPerRaid", 0.1f));
            damagePerRaid = Math.Max(0f, Config.Float("damagePerRaid", 0.1f));

            configured.Clear();
            if (Config.Raw("raids")?.Token is JArray arr)
                foreach (var token in arr.OfType<JObject>())
                    configured.Add(new Raid { Name = token.Value<string>("name"), Mobs = MobLines.Parse(token["mobs"]) });
            Config.WarnUnknown();
        }

        public override void OnBeforeStart()
        {
            raidIndex = -1;
            queue.Clear();
            raids = configured
                .Select(r => new Raid { Name = r.Name, Mobs = r.Mobs.Where(e => MobLines.Usable(runningEvent.sapi, e, runningEvent.Event?.Code, log)).ToList() })
                .Where(r => r.Mobs.Count > 0)
                .ToList();
            if (raids.Count == 0)
            {
                log.Warning("{0}: no raid has a mob that can be spawned here, there will be no raids.", runningEvent.Event?.Code);
                return;
            }
            spawnPoints.Load(runningEvent, log);
            runningEvent.Mobs.SetRallyPoints(spawnPoints.All);
        }

        public override void OnStart()
        {
            nextRaidAt = runningEvent.ElapsedSeconds + firstRaidSeconds;
            hooks.HookAll(runningEvent, runningEvent.ParticipantsBefore, MobDamage, includeBots: true);
        }

        // Mob hits grow with the raid number; the mode's own hooks see the rest.
        private float MobDamage(string victimUid, float damage, DamageSource source)
        {
            var cause = source?.GetCauseEntity();
            if (!MatchMobLink.IsMatchMob(cause)) return damage;
            return MatchMobLink.MatchIdOf(cause) == runningEvent.Id ? damage * MatchMobLink.DamageMulOf(cause) : 0f;
        }

        public override void OnParticipantJoinedGame() => hooks.RehookAll(runningEvent);

        public override void OnTick(float dt)
        {
            if (raids.Count == 0 || runningEvent.EventState != EventState.Running) return;
            float now = runningEvent.ElapsedSeconds;

            // A truce (Hunger Games' opening) only postpones the raid.
            if (runningEvent.CombatSuspended)
            {
                if (nextRaidAt < now + 5f) nextRaidAt = now + 5f;
                return;
            }

            if (now >= nextRaidAt) StartRaid(now);
            SpawnDue(now);
        }

        private void StartRaid(float now)
        {
            raidIndex++;
            nextRaidAt = now + intervalSeconds;
            nextSpawnAt = now;

            var raid = raids[Math.Min(raidIndex, raids.Count - 1)];
            int fighters = Math.Max(1, runningEvent.ParticipantsStillInEvent.Count);
            int before = queue.Count;
            foreach (var entry in raid.Mobs)
                for (int i = 0, n = MobLines.CountWithMutators(runningEvent.Event, entry.CountFor(fighters)); i < n; i++)
                    queue.Enqueue(entry);

            if (string.IsNullOrEmpty(raid.Name)) Announce("canevents:raid-start", queue.Count - before);
            else Announce("canevents:raid-start-named", raid.Name, queue.Count - before);
            hooks.HookAll(runningEvent, runningEvent.ParticipantsBefore, MobDamage, includeBots: true);
            hooks.RehookAll(runningEvent);
        }

        private void SpawnDue(float now)
        {
            var mobs = runningEvent.Mobs;
            var fighters = runningEvent.LiveBodies().Select(b => b.body).ToList();
            while (queue.Count > 0 && nextSpawnAt <= now && mobs.AliveCount < maxAliveMobs)
            {
                var entry = queue.Dequeue();
                nextSpawnAt += spawnIntervalSeconds;
                float step = Math.Max(0, raidIndex);
                var at = spawnPoints.Pick(entry.SpawnTag, fighters, runningEvent.sapi.World.Rand);
                if (at == null || !mobs.Spawn(entry, at, entry.HealthMul * (1f + healthPerRaid * step), entry.DamageMul * (1f + damagePerRaid * step)))
                    log.Warning("Could not spawn {0} for raid {1}.", entry.Describe(), raidIndex + 1);
            }
            if (nextSpawnAt < now) nextSpawnAt = now;
        }

        // A mob kill pays shop points like any kill.
        public void OnMobDeath(Entity mob, DamageSource source)
        {
            if (DamageHookSet.TryParticipantAttacker(runningEvent, source, out var killerUid, includeBots: true))
                MatchShopEB.Of(runningEvent)?.OnKill(killerUid);
        }

        public override void OnEnd()
        {
            hooks.UnhookAll();
            queue.Clear();
        }
    }
}
