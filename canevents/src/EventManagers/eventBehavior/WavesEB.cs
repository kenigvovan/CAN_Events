using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers.eventBehavior.abstracts;
using canevents.src.EventManagers.eventBehavior.Shared;
using canevents.src.EventManagers.events;
using canevents.src.EventPlacesManagers.Places;
using canevents.src.Mobs;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace canevents.src.EventManagers.eventBehavior
{
    // Co-op survival: everyone on the roster (players and filler bots) against waves of match mobs.
    // Clear the last wave to win; the whole side down, or a wave over its time limit, loses.
    // The fallen watch until the wave is over and come back, re-kitted, in the intermission.
    //
    // Mob spawn points: /caneventplace mobspawn [tag]. Without any, the player spawns are used.
    // Wave lines (MobEntry): code / rpg / rpgGroup, count, perPlayer, healthMul, damageMul, spawn.
    // A line no installed mod can provide is skipped with a warning; the rest of its wave still comes.
    public class WavesEB : KillScoringEB, IDecidesOnDropout, IRoundClock, IStartCryProvider, IBotCombatRoles, IMatchMobListener
    {
        private sealed class Wave
        {
            public string Name;
            public List<MobEntry> Mobs = new List<MobEntry>();
            public float SpawnIntervalSeconds;
            public float TimeLimitSeconds;
            // A boss wave is won by killing the boss; whatever is left of its adds goes with it.
            public MobEntry Boss;
            public List<BossPhase> Phases = new List<BossPhase>();
        }

        // Fires once when the boss drops to AtHealth (fraction of max): adds come, it hits harder.
        private sealed class BossPhase
        {
            public float AtHealth;
            public List<MobEntry> Adds = new List<MobEntry>();
            public float DamageMul = 1f;
            public string Message;
        }

        private readonly List<Wave> configuredWaves = new List<Wave>();
        private List<Wave> waves = new List<Wave>();
        private readonly MobSpawnPoints spawnPoints = new MobSpawnPoints();
        private readonly Queue<MobEntry> spawnQueue = new Queue<MobEntry>();

        private float intermissionSeconds = 10f;
        private float defaultWaveTimeLimit = 300f;
        private float defaultSpawnInterval = 0.5f;
        private int maxAliveMobs = 30;
        private float healthPerPlayer = 0.25f;
        private float healthPerWave;
        private float damagePerWave = 0.1f;
        private float mobSpread = 4f;
        private bool friendlyFire;
        private bool announceMobKills;

        private int waveIndex = -1;
        private int wavesCleared;
        private bool waveLive;
        private float waveStartedAt;
        private float nextSpawnAt;
        private float intermissionEndsAt = -1f;
        private int fightersAtWaveStart = 1;

        private readonly DamageHookSet hooks = new DamageHookSet();

        public WavesEB(string name, JsonObject attributes, RunningEvent runningEvent)
            : base(name, attributes, runningEvent) { }

        public override string Name => BehaviorNames.Waves;

        public override bool WantsTick => true;

        public bool RoundLive => waveLive;
        public float RoundLength => CurrentWave?.TimeLimitSeconds ?? 0f;
        public float RoundSecondsLeft => RoundLength <= 0f ? 0f
            : Math.Max(0f, RoundLength - (runningEvent.ElapsedSeconds - waveStartedAt));
        public string StartCryLangKey => "canevents:waves-start-cry";

        private Wave CurrentWave => waveIndex >= 0 && waveIndex < waves.Count ? waves[waveIndex] : null;

        // Everyone on the roster is one side; only mobs are enemies.
        public bool MayAttack(string attackerUid, string victimUid) => false;

        protected override void ReadModeConfig()
        {
            intermissionSeconds = Math.Max(3f, Config.Float("intermissionSeconds", 10f));
            defaultWaveTimeLimit = Math.Max(0f, Config.Float("waveTimeLimitSeconds", 300f));
            defaultSpawnInterval = Math.Max(0f, Config.Float("spawnIntervalSeconds", 0.5f));
            maxAliveMobs = Math.Max(1, Config.Int("maxAliveMobs", 30));
            healthPerPlayer = Math.Max(0f, Config.Float("healthPerPlayer", 0.25f));
            healthPerWave = Math.Max(0f, Config.Float("healthPerWave", 0f));
            damagePerWave = Math.Max(0f, Config.Float("damagePerWave", 0.1f));
            mobSpread = Math.Max(0f, Config.Float("mobSpread", 4f));
            friendlyFire = Config.Bool("friendlyFire", false);
            announceMobKills = Config.Bool("announceMobKills", false);
            ParseWaves();
        }

        private void ParseWaves()
        {
            configuredWaves.Clear();
            if (!(Config.Raw("waves")?.Token is JArray arr)) return;

            foreach (var token in arr.OfType<JObject>())
            {
                var wave = new Wave
                {
                    Name = token.Value<string>("name"),
                    SpawnIntervalSeconds = Math.Max(0f, token.Value<float?>("spawnIntervalSeconds") ?? defaultSpawnInterval),
                    TimeLimitSeconds = Math.Max(0f, token.Value<float?>("timeLimitSeconds") ?? defaultWaveTimeLimit),
                };
                wave.Mobs.AddRange(ParseLines(token["mobs"]));
                wave.Boss = MobEntry.Parse(token["boss"] as JObject);
                if (token["phases"] is JArray phases)
                    foreach (var p in phases.OfType<JObject>())
                        wave.Phases.Add(new BossPhase
                        {
                            AtHealth = GameMath.Clamp(p.Value<float?>("atHealth") ?? 0.5f, 0.01f, 0.99f),
                            Adds = ParseLines(p["adds"]),
                            DamageMul = Math.Max(0.1f, p.Value<float?>("damageMul") ?? 1f),
                            Message = p.Value<string>("message"),
                        });
                wave.Phases.Sort((a, b) => b.AtHealth.CompareTo(a.AtHealth));
                configuredWaves.Add(wave);
            }
        }

        private static List<MobEntry> ParseLines(JToken token) => MobLines.Parse(token);

        public override void OnBeforeStart()
        {
            InitScores();
            waves = UsableWaves();
            if (waves.Count == 0)
            {
                log.Error("{0}: no wave has a mob that can be spawned here, the match is called off.", runningEvent.Event?.Code);
                runningEvent.RequestFinish("no usable waves");
                return;
            }

            spawnPoints.Load(runningEvent, log);
            runningEvent.Mobs.SpreadPenalty = mobSpread;
            runningEvent.Mobs.SetRallyPoints(spawnPoints.All);

            float perWave = waves.Sum(w => (w.TimeLimitSeconds > 0f ? w.TimeLimitSeconds : 600f) + intermissionSeconds);
            runningEvent.SetDuration(perWave + intermissionSeconds + 60f);
        }

        // Lines no installed mod provides are dropped; a wave left empty is skipped.
        private List<Wave> UsableWaves()
        {
            var result = new List<Wave>();
            foreach (var wave in configuredWaves)
            {
                var usable = new Wave
                {
                    Name = wave.Name,
                    SpawnIntervalSeconds = wave.SpawnIntervalSeconds,
                    TimeLimitSeconds = wave.TimeLimitSeconds,
                    Mobs = wave.Mobs.Where(Usable).ToList(),
                };
                // Without its boss the phases mean nothing; the rest of the wave still comes.
                if (wave.Boss != null && Usable(wave.Boss))
                {
                    usable.Boss = wave.Boss;
                    usable.Phases = wave.Phases.Select(p => new BossPhase
                    {
                        AtHealth = p.AtHealth, DamageMul = p.DamageMul, Message = p.Message, Adds = p.Adds.Where(Usable).ToList(),
                    }).ToList();
                }
                if (usable.Mobs.Count > 0 || usable.Boss != null) result.Add(usable);
            }
            return result;
        }

        private bool Usable(MobEntry entry) => MobLines.Usable(runningEvent.sapi, entry, runningEvent.Event?.Code, log);

        public override void OnStart()
        {
            if (waves.Count == 0) return;
            GiveLoadoutAndHealAll();
            HookFighters();
            BeginIntermission(firstWave: true);
        }

        // Rehook: a fallen bot comes back in a new body.
        private void HookFighters()
        {
            hooks.HookAll(runningEvent, runningEvent.ParticipantsBefore.Where(CanPlayOn), IncomingDamage, includeBots: true);
            hooks.RehookAll(runningEvent);
        }

        private float IncomingDamage(string victimUid, float damage, DamageSource source)
        {
            if (!friendlyFire && DamageHookSet.TryHostileHit(runningEvent, victimUid, damage, source, out _, includeBots: true))
                return 0f;

            var cause = source?.GetCauseEntity();
            if (!MatchMobLink.IsMatchMob(cause)) return damage;
            // Another match's mob has no business here.
            return MatchMobLink.MatchIdOf(cause) == runningEvent.Id ? damage * MatchMobLink.DamageMulOf(cause) : 0f;
        }

        public override void OnParticipantJoinedGame() => hooks.RehookAll(runningEvent);

        public override void OnTick(float dt)
        {
            if (MatchResolved || waves.Count == 0) return;

            if (!waveLive)
            {
                if (intermissionEndsAt >= 0f && runningEvent.ElapsedSeconds >= intermissionEndsAt) StartWave();
                return;
            }

            if (!runningEvent.ParticipantsBefore.Any(CanPlayOn))
            {
                Defeat("canevents:waves-abandoned");
                return;
            }
            if (!runningEvent.ParticipantsStillInEvent.Any())
            {
                Defeat("canevents:waves-wiped");
                return;
            }

            var wave = CurrentWave;
            if (wave.TimeLimitSeconds > 0f && runningEvent.ElapsedSeconds - waveStartedAt >= wave.TimeLimitSeconds)
            {
                Defeat("canevents:waves-time-up");
                return;
            }

            TickBoss(wave);
            SpawnDue(wave);
            if (spawnQueue.Count == 0 && runningEvent.Mobs.AliveCount == 0 && runningEvent.Mobs.PendingCount == 0) EndWave();
        }

        private Entity boss;
        private string bossName;
        private int nextPhase;
        private int bossPercentSent = -1;

        public override ScoreboardData BuildScoreboard()
        {
            var data = base.BuildScoreboard();
            var health = boss?.Alive == true ? boss.GetBehavior<Vintagestory.GameContent.EntityBehaviorHealth>() : null;
            if (health != null && health.MaxHealth > 0f)
            {
                data.BossName = bossName;
                data.BossHealth = GameMath.Clamp(health.Health / health.MaxHealth, 0f, 1f);
            }
            return data;
        }

        private void SpawnBoss(Wave wave)
        {
            boss = null;
            nextPhase = 0;
            var at = PickSpawn(wave.Boss.SpawnTag);
            if (at == null || !runningEvent.Mobs.Spawn(wave.Boss, at, HealthMulFor(wave.Boss), DamageMulFor(wave.Boss), body =>
                {
                    boss = body;
                    bossName = wave.Boss.Name ?? body.GetName();
                    Announce("canevents:boss-appears", bossName);
                }))
                log.Warning("Could not spawn the boss {0} for wave {1}.", wave.Boss.Describe(), waveIndex + 1);
        }

        // Health in the nametag stands in for a boss bar; phases fire as it drops.
        private void TickBoss(Wave wave)
        {
            if (boss == null || !boss.Alive) return;
            var health = boss.GetBehavior<Vintagestory.GameContent.EntityBehaviorHealth>();
            if (health == null || health.MaxHealth <= 0f) return;
            float left = health.Health / health.MaxHealth;
            boss.GetBehavior<Vintagestory.GameContent.EntityBehaviorNameTag>()?.SetName($"{bossName} {Math.Ceiling(left * 100)}%");
            // The HUD bar rides on the scoreboard; only resent when the percent moves.
            int percent = (int)Math.Ceiling(left * 100);
            if (percent != bossPercentSent)
            {
                bossPercentSent = percent;
                PushScoreboard();
            }

            while (nextPhase < wave.Phases.Count && left <= wave.Phases[nextPhase].AtHealth)
            {
                var phase = wave.Phases[nextPhase++];
                MatchMobLink.SetDamageMul(boss, MatchMobLink.DamageMulOf(boss) * phase.DamageMul);
                Enqueue(phase.Adds);
                Announce(phase.Message ?? "canevents:boss-phase", bossName, nextPhase);
            }
        }

        private void Enqueue(IEnumerable<MobEntry> lines)
        {
            foreach (var entry in lines)
                for (int i = 0, n = MobLines.CountWithMutators(runningEvent.Event, entry.CountFor(fightersAtWaveStart)); i < n; i++)
                    spawnQueue.Enqueue(entry);
        }

        private void StartWave()
        {
            intermissionEndsAt = -1f;
            runningEvent.CombatSuspended = false;
            waveIndex++;
            var wave = CurrentWave;

            fightersAtWaveStart = Math.Max(1, runningEvent.ParticipantsStillInEvent.Count);
            spawnQueue.Clear();
            Enqueue(wave.Mobs);

            // Fresh kit for everyone, upgrades and purchases from the pause included.
            foreach (var player in runningEvent.ActiveOnlineParticipants)
            {
                if (!runningEvent.KeepOwnGear) ClearForNextRound(player);
                GiveLoadoutAndHeal(player);
            }

            // Bot bodies brought back in the intermission may only exist now.
            HookFighters();
            waveLive = true;
            waveStartedAt = runningEvent.ElapsedSeconds;
            nextSpawnAt = waveStartedAt;
            boss = null;
            if (wave.Boss != null) SpawnBoss(wave);

            if (string.IsNullOrEmpty(wave.Name)) Announce("canevents:waves-wave-start", waveIndex + 1, waves.Count, spawnQueue.Count);
            else Announce("canevents:waves-wave-start-named", waveIndex + 1, waves.Count, wave.Name, spawnQueue.Count);
            PushScoreboard();
            runningEvent.EndHold();
            runningEvent.NetworkHandler?.SendStateUpdate(runningEvent);
        }

        // Staggered so a big wave doesn't land in one tick; capped so the server isn't flooded.
        private void SpawnDue(Wave wave)
        {
            float now = runningEvent.ElapsedSeconds;
            var mobs = runningEvent.Mobs;
            while (spawnQueue.Count > 0 && nextSpawnAt <= now && mobs.AliveCount < maxAliveMobs)
            {
                var entry = spawnQueue.Dequeue();
                nextSpawnAt += wave.SpawnIntervalSeconds;

                var at = PickSpawn(entry.SpawnTag);
                if (at == null || !mobs.Spawn(entry, at, HealthMulFor(entry), DamageMulFor(entry)))
                    log.Warning("Could not spawn {0} for wave {1}.", entry.Describe(), waveIndex + 1);
            }
            // Don't build up a burst while the cap holds the queue back.
            if (nextSpawnAt < now) nextSpawnAt = now;
        }

        private float HealthMulFor(MobEntry entry)
            => entry.HealthMul * (1f + healthPerPlayer * (fightersAtWaveStart - 1)) * (1f + healthPerWave * waveIndex);

        private float DamageMulFor(MobEntry entry)
            => entry.DamageMul * (1f + damagePerWave * waveIndex);

        private BlockPos PickSpawn(string tag)
            => spawnPoints.Pick(tag, runningEvent.LiveBodies().Select(b => b.body).ToList(), runningEvent.sapi.World.Rand);

        public void OnMobDeath(Entity mob, DamageSource source)
        {
            if (boss != null && mob.EntityId == boss.EntityId)
            {
                boss = null;
                bossPercentSent = -1;
                spawnQueue.Clear();
                Announce("canevents:boss-down", bossName);
                // The rest falls with its leader; the wave ends on the next tick.
                runningEvent.Mobs.DespawnRest();
                PushScoreboard();
            }
            if (!DamageHookSet.TryParticipantAttacker(runningEvent, source, out var killerUid, includeBots: true)) return;
            if (killCounts.ContainsKey(killerUid)) killCounts[killerUid]++;
            MatchShopEB.Of(runningEvent)?.OnKill(killerUid);
            if (announceMobKills) SendKillFeed(runningEvent.NameOf(killerUid), mob.GetName(), "killed");
            PushScoreboard();
        }

        // The fallen sit out the rest of the wave.
        protected override void AfterScoredDeath(string victimUid, Entity victimBody, KillInfo kill)
            => runningEvent.RemoveFromPlay(victimUid);

        private void EndWave()
        {
            waveLive = false;
            wavesCleared++;
            MatchShopEB.Of(runningEvent)?.OnRoundWon(Stayers());

            if (wavesCleared >= waves.Count)
            {
                Announce("canevents:waves-victory", wavesCleared);
                ResolveMatch(Stayers(), "all waves cleared");
                return;
            }
            Announce("canevents:waves-wave-cleared", wavesCleared, waves.Count);
            PushScoreboard();
            BeginIntermission();
        }

        private void Defeat(string langKey)
        {
            waveLive = false;
            spawnQueue.Clear();
            Announce(langKey, waveIndex + 1, wavesCleared);
            ResolveMatch(null, "waves lost");
        }

        // Not sent home and not quit. A bot always is.
        private bool CanPlayOn(string uid)
            => runningEvent.HoldsPlayer(uid) && !runningEvent.LeftInPlay(uid)
               && (Bots.BotIdentity.IsBot(uid) || runningEvent.sapi.World.PlayerByUid(uid) is IServerPlayer);

        private List<string> Stayers() => runningEvent.ParticipantsBefore.Where(CanPlayOn).ToList();

        // firstWave: everyone was just teleported in with a fresh kit, only the countdown runs.
        private void BeginIntermission(bool firstWave = false)
        {
            intermissionEndsAt = runningEvent.ElapsedSeconds + intermissionSeconds;
            runningEvent.BeginHold(intermissionSeconds, "canevents:waves-next-wave");
            runningEvent.CombatSuspended = true;

            if (!firstWave)
            {
                IssuedGearSweep.Sweep(runningEvent.sapi, runningEvent.Place, log);
                var tp = runningEvent.GetBehavior<TPThereAndBack>();
                var bots = runningEvent.GetBehavior<ArenaBotsEB>();
                foreach (var uid in runningEvent.ParticipantsBefore.ToList())
                {
                    if (!CanPlayOn(uid)) continue;
                    bool fell = !runningEvent.IsActive(uid);
                    if (Bots.BotIdentity.IsBot(uid))
                    {
                        if (!fell) continue;
                        runningEvent.ReturnToPlay(uid);
                        bots?.ResetForRound(uid);
                        continue;
                    }

                    var player = (IServerPlayer)runningEvent.sapi.World.PlayerByUid(uid);
                    if (fell) Revive(player, tp);
                    // Emptied now, the full kit with whatever was bought comes at the wave start.
                    if (!runningEvent.KeepOwnGear) ClearForNextRound(player);
                    EventBroadcaster.Heal(player);
                    EventBroadcaster.Feed(player);
                }
                HookFighters();
            }

            // The pause is the time to spend points: the window opens by itself.
            var shop = MatchShopEB.Of(runningEvent);
            if (shop != null)
                foreach (var uid in Stayers().Where(u => !Bots.BotIdentity.IsBot(u))) shop.Push(uid, open: true);

            runningEvent.NetworkHandler?.SendStateUpdate(runningEvent);
        }

        private void Revive(IServerPlayer player, TPThereAndBack tp)
        {
            runningEvent.Spectator?.RemoveSpectator(player);
            if (player.Entity != null && !player.Entity.Alive) player.Entity.Revive();
            Compat.DownedCompat.Clear(player);
            runningEvent.ReturnToPlay(player.PlayerUID);
            tp?.SendToSpawn(player, "next wave ");
        }

        public override void OnBeforeFinish()
        {
            if (MatchResolved) return;
            // Admin stop or the time backstop: nothing was won.
            ResolveMatch(null, "waves not finished", requestFinish: false);
        }

        public override void OnEnd()
        {
            hooks.UnhookAll();
            runningEvent.CombatSuspended = false;
            if (waveIndex >= 0) Announce("canevents:waves-ended", wavesCleared, waves.Count);
        }

        // Victory: everyone who stayed shares first place. A loss ranks nobody, so only participation pays.
        public override List<HashSet<string>> GetFinalRanking()
        {
            var winners = runningEvent.GetWinners();
            return winners.Count > 0 ? new List<HashSet<string>> { new HashSet<string>(winners) } : new List<HashSet<string>>();
        }
    }
}
