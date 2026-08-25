using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers.eventBehavior.abstracts;
using canevents.src.EventManagers.eventBehavior.Shared;
using canevents.src.EventManagers.events;
using canevents.src.HarmonyPatches;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace canevents.src.EventManagers.eventBehavior
{
    // FFA "Juggernaut": the crowned player is buffed (max health + melee damage) and players respawn
    // on death. A point scores for kills landed WHILE crowned and for dethroning the juggernaut,
    // which transfers the crown. Win = first to scoreLimit, else highest score at the time limit.
    // Runs on DEATHMATCH places; buff via EntityBehaviorHealth.SetMaxHealthModifiers + meleeWeaponsDamage stat.
    public class JuggernautEB : KillScoringEB, IProvidesRespawnPoint
    {
        private const string BuffKey = "juggernaut";

        private int scoreLimit = 25;
        private float bonusHealth = 20f;   // flat extra max-health while crowned
        private float damageBonus = 0.5f;  // WeightedSum delta on meleeWeaponsDamage (0.5 => +50%)

        // Competition score (jug-kills + dethrones), distinct from raw killCounts used for stats.
        private readonly Dictionary<string, int> jugScore = new Dictionary<string, int>();
        private string currentJugUid;

        public JuggernautEB(string name, JsonObject attributes, RunningEvent runningEvent)
            : base(name, attributes, runningEvent) { }

        public override string Name { get => "Juggernaut"; set { } }

        public override void Initialize()
        {
            ParseTimeLimit(600f);
            scoreLimit = Config.Int("scoreLimit", 25);
            bonusHealth = Config.Float("juggernautBonusHealth", 20f);
            damageBonus = Config.Float("juggernautDamageBonus", 0.5f);
            ParseLoadoutSets();
            Config.WarnUnknown();
        }

        public override void OnBeforeStart()
        {
            BeginMatch();
            jugScore.Clear();
            foreach (var uid in runningEvent.ParticipantsBefore) jugScore[uid] = 0;
            currentJugUid = null;   // InitScores() clears the resolution latch
        }

        public override void OnStart()
        {
            GiveLoadoutAndHealAll();
            var jug = PickRandomActivePlayer();
            if (jug != null)
            {
                Crown(jug);
                EventBroadcaster.Broadcast(runningEvent,
                    Lang.Get("canevents:juggernaut-crowned", jug.PlayerName));
            }
            PushScoreboard();
        }

        protected override void OnScoredDeath(IServerPlayer victim, KillInfo kill)
        {
            string victimUid = victim.PlayerUID;
            bool victimWasJug = victimUid == currentJugUid;
            bool cleanKill = kill.HasKiller && !kill.TeamKill && kill.KillerUid != victimUid;

            // No EliminatePlayer: players respawn (IProvidesRespawnPoint + OnParticipantRespawn).

            if (cleanKill && victimWasJug)
            {
                // Dethrone: killer scores and takes the crown.
                AddScore(kill.KillerUid);
                RemoveCurrentBuff();
                var killer = runningEvent.sapi.World.PlayerByUid(kill.KillerUid) as IServerPlayer;
                if (killer != null)
                {
                    Crown(killer);
                    EventBroadcaster.Broadcast(runningEvent,
                        Lang.Get("canevents:juggernaut-dethroned", killer.PlayerName, victim.PlayerName));
                }
                else
                {
                    // Killer went offline this instant, keep the match crowned.
                    ReassignCrown();
                }
            }
            else if (cleanKill && kill.KillerUid == currentJugUid)
            {
                AddScore(kill.KillerUid);
                EventBroadcaster.Broadcast(runningEvent,
                    Lang.Get("canevents:juggernaut-slain", kill.KillerName, victim.PlayerName));
            }
        }

        protected override void AfterScoredDeath(IServerPlayer victim, KillInfo kill)
        {
            bool cleanKill = kill.HasKiller && !kill.TeamKill && kill.KillerUid != victim.PlayerUID;
            if (cleanKill) TryDeclareScoreLimitWinner(kill.KillerUid);
        }

        public override void OnParticipantRespawn(IServerPlayer player)
        {
            if (!runningEvent.IsActive(player.PlayerUID)) return;
            // TPThereAndBack teleports on its own OnParticipantRespawn; we re-equip + heal.
            GiveLoadoutAndHeal(player);
            // Killed by the environment (nobody to dethrone them): they keep the crown, so
            // re-assert the buff and top up to the buffed max.
            if (player.PlayerUID == currentJugUid) ApplyBuff(player);
        }

        public override void OnParticipantLeftGame()
        {
            if (MatchResolved || currentJugUid == null) return;
            bool jugStillActive = runningEvent.IsActive(currentJugUid)
                && runningEvent.sapi.World.PlayerByUid(currentJugUid) != null;
            if (!jugStillActive) ReassignCrown();
        }

        // Manager owns the time-up trigger (MaxDurationSeconds); resolve the leader when it fires.
        public override void OnBeforeFinish()
        {
            if (MatchResolved) return;
            string leader = ResolveLeader();
            DeclareWinner(leader != null ? new[] { leader } : null);
        }

        public override void OnEnd()
        {
            RemoveCurrentBuff(); // CRITICAL: never leave a player buffed after the match ends.

            var winners = runningEvent.GetWinners();
            if (winners.Count > 0)
            {
                var entries = winners.Select(uid =>
                {
                    var pl = runningEvent.sapi.World.PlayerByUid(uid);
                    int sc = jugScore.TryGetValue(uid, out var v) ? v : 0;
                    return Lang.Get("canevents:juggernaut-winner-entry", pl?.PlayerName ?? uid, sc);
                });
                EventBroadcaster.Broadcast(runningEvent,
                    Lang.Get("canevents:juggernaut-ended", string.Join(", ", entries)));
            }
            else
            {
                EventBroadcaster.Broadcast(runningEvent, Lang.Get("canevents:juggernaut-draw"));
            }
        }

        // Scoreboard shows the competition score, not raw kills, so the HUD matches the standings.
        protected override void PushScoreboard()
            => runningEvent.NetworkHandler?.SendScoreboard(runningEvent, jugScore, deathCounts);

        // FFA placement by competition score (desc), with raw kills then fewest deaths as tiebreaks.
        public override List<HashSet<string>> GetFinalRanking()
        {
            return ScoringUtil.RankIndividually(runningEvent.ParticipantsBefore,
                ScoreOf,
                uid => killCounts.TryGetValue(uid, out var k) ? k : 0,
                uid => deathCounts.TryGetValue(uid, out var d) ? d : 0);
        }

        public bool TryGetRespawnPoint(string playerUid, out PlayerSpawnPos pos)
            => EventSpawns.TryGetRandom(runningEvent, out pos);

        // Crown + buff helpers

        private void AddScore(string uid)
        {
            jugScore.TryGetValue(uid, out var s);
            jugScore[uid] = s + 1;
        }

        private void TryDeclareScoreLimitWinner(string uid)
        {
            if (MatchResolved || scoreLimit <= 0) return;
            if (jugScore.TryGetValue(uid, out var s) && s >= scoreLimit)
                DeclareWinner(new[] { uid });
        }

        private void DeclareWinner(IEnumerable<string> winners)
            => ResolveMatch(winners, "juggernaut score limit or resolution");

        // Single highest scorer, or null on a tie / no participants.
        private string ResolveLeader()
            => ScoringUtil.TryArgMax(runningEvent.ParticipantsBefore, ScoreOf, out var best) ? best : null;

        private int ScoreOf(string uid) => jugScore.TryGetValue(uid, out var v) ? v : 0;

        private void Crown(IServerPlayer p)
        {
            currentJugUid = p.PlayerUID;
            ApplyBuff(p);
        }

        private void ReassignCrown()
        {
            RemoveCurrentBuff();
            var jug = PickRandomActivePlayer();
            if (jug == null) return;
            Crown(jug);
            EventBroadcaster.Broadcast(runningEvent,
                Lang.Get("canevents:juggernaut-reassigned", jug.PlayerName));
        }

        private void RemoveCurrentBuff()
        {
            if (currentJugUid == null) return;
            var old = runningEvent.sapi.World.PlayerByUid(currentJugUid) as IServerPlayer;
            if (old != null) RemoveBuff(old);
            currentJugUid = null;
        }

        private void ApplyBuff(IServerPlayer p)
        {
            var hb = p.Entity?.GetBehavior<EntityBehaviorHealth>();
            hb?.SetMaxHealthModifiers(BuffKey, bonusHealth); // recomputes MaxHealth internally
            EventBroadcaster.Heal(p);                        // top up to the new buffed max
            p.Entity?.Stats.Set("meleeWeaponsDamage", BuffKey, damageBonus, false);
        }

        private void RemoveBuff(IServerPlayer p)
        {
            var hb = p.Entity?.GetBehavior<EntityBehaviorHealth>();
            hb?.SetMaxHealthModifiers(BuffKey, 0f);
            p.Entity?.Stats.Remove("meleeWeaponsDamage", BuffKey);
        }

        private IServerPlayer PickRandomActivePlayer()
        {
            // Exclude corpses: Juggernaut never eliminates, so a just-killed player briefly stays in
            // the active set, and crowning them would buff a dead body.
            var candidates = runningEvent.ParticipantsStillInEvent
                .Select(uid => runningEvent.sapi.World.PlayerByUid(uid) as IServerPlayer)
                .Where(pl => pl?.Entity != null && pl.Entity.Alive)
                .ToList();
            if (candidates.Count == 0) return null;
            return candidates[runningEvent.sapi.World.Rand.Next(candidates.Count)];
        }
    }
}
