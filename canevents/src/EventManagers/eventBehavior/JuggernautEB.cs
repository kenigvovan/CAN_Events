using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers.eventBehavior.abstracts;
using canevents.src.EventManagers.eventBehavior.Shared;
using canevents.src.EventManagers.events;
using canevents.src.HarmonyPatches;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace canevents.src.EventManagers.eventBehavior
{
    // FFA with respawns. Points for kills while crowned and for killing the juggernaut (takes the crown).
    // Buff via EntityBehaviorHealth.SetMaxHealthModifiers + meleeWeaponsDamage stat.
    public class JuggernautEB : RespawningKillScoringEB
    {
        private const string BuffKey = "juggernaut";

        private int scoreLimit = 25;
        private float bonusHealth = 20f;
        private float damageBonus = 0.5f;  // WeightedSum delta, 0.5 = +50%

        // Not the same as killCounts.
        private readonly Dictionary<string, int> jugScore = new Dictionary<string, int>();
        private string currentJugUid;

        public JuggernautEB(string name, JsonObject attributes, RunningEvent runningEvent)
            : base(name, attributes, runningEvent) { }

        public override string Name => BehaviorNames.Juggernaut;

        protected override float DefaultTimeLimitSeconds => 600f;

        protected override void ReadModeConfig()
        {
            scoreLimit = Config.Int(BehaviorConfigKeys.ScoreLimit, 25);
            bonusHealth = Config.Float("juggernautBonusHealth", 20f);
            damageBonus = Config.Float("juggernautDamageBonus", 0.5f);
        }

        public override void OnBeforeStart()
        {
            BeginMatch();
            jugScore.Clear();
            foreach (var uid in runningEvent.ParticipantsBefore) jugScore[uid] = 0;
            currentJugUid = null;
        }

        public override void OnStart()
        {
            GiveLoadoutAndHealAll();
            var jug = PickRandomActive();
            if (jug != null)
            {
                Crown(jug);
                Announce("canevents:juggernaut-crowned", runningEvent.NameOf(jug));
            }
            PushScoreboard();
        }

        protected override void OnScoredDeath(string victimUid, Entity victimBody, KillInfo kill)
        {
            bool victimWasJug = victimUid == currentJugUid;
            bool cleanKill = kill.IsClean;
            string victimName = runningEvent.NameOf(victimUid);

            if (cleanKill && victimWasJug)
            {
                AddScore(kill.KillerUid);
                RemoveCurrentBuff(victimBody);
                var killerBody = runningEvent.BodyOf(kill.KillerUid);
                if (killerBody != null && killerBody.Alive)
                {
                    Crown(kill.KillerUid);
                    Announce("canevents:juggernaut-dethroned", runningEvent.NameOf(kill.KillerUid), victimName);
                }
                else
                {
                    // Killer left or died in the same instant.
                    ReassignCrown();
                }
            }
            else if (cleanKill && kill.KillerUid == currentJugUid)
            {
                AddScore(kill.KillerUid);
                Announce("canevents:juggernaut-slain", kill.KillerName, victimName);
            }
        }

        protected override void AfterScoredDeath(string victimUid, Entity victimBody, KillInfo kill)
        {
            if (kill.IsClean) TryDeclareScoreLimitWinner(kill.KillerUid);

            // Bots don't respawn, pass the crown on.
            if (!MatchResolved && victimUid == currentJugUid && Bots.BotIdentity.IsBot(victimUid))
            {
                RemoveCurrentBuff(victimBody);
                ReassignCrown();
            }
        }

        // Environment death keeps the crown; reapply the buff after respawn.
        protected override void AfterRespawnEquipped(IServerPlayer player)
        {
            if (player.PlayerUID == currentJugUid) ApplyBuff(player.Entity);
        }

        public override void OnParticipantLeftGame()
        {
            if (MatchResolved || currentJugUid == null) return;
            bool jugStillActive = runningEvent.IsActive(currentJugUid)
                && runningEvent.BodyOf(currentJugUid) != null;
            if (!jugStillActive) ReassignCrown();
        }

        public override void OnBeforeFinish()
        {
            if (MatchResolved) return;
            string leader = ResolveLeader();
            DeclareWinner(leader != null ? new[] { leader } : null);
        }

        public override void OnEnd()
        {
            RemoveCurrentBuff();

            AnnounceWinners("canevents:juggernaut-ended", "canevents:juggernaut-draw",
                uid => Lang.Get("canevents:juggernaut-winner-entry", runningEvent.NameOf(uid), ScoreOf(uid)));
        }

        // Kills column shows jugScore, not raw kills.
        public override ScoreboardData BuildScoreboard()
            => new ScoreboardData { Kills = jugScore, Deaths = deathCounts };

        public override List<HashSet<string>> GetFinalRanking()
        {
            return ScoringUtil.RankIndividually(runningEvent.ParticipantsBefore,
                ScoreOf,
                KillsOf,
                DeathsOf);
        }

        private void AddScore(string uid)
        {
            jugScore.TryGetValue(uid, out var s);
            jugScore[uid] = s + 1;
        }

        private void TryDeclareScoreLimitWinner(string uid)
        {
            if (MatchResolved || scoreLimit <= 0) return;
            if (ScoreOf(uid) >= scoreLimit) DeclareWinner(new[] { uid });
        }

        private void DeclareWinner(IEnumerable<string> winners)
            => ResolveMatch(winners, "juggernaut score limit or resolution");

        // Null on a tie.
        private string ResolveLeader()
            => ScoringUtil.TryArgMax(runningEvent.ParticipantsBefore, ScoreOf, out var best) ? best : null;

        private int ScoreOf(string uid) => ScoringUtil.CountOf(jugScore, uid);

        // BodyOf so bots can be crowned too.
        private void Crown(string uid)
        {
            currentJugUid = uid;
            ApplyBuff(runningEvent.BodyOf(uid));
        }

        private void ReassignCrown()
        {
            RemoveCurrentBuff();
            var jug = PickRandomActive();
            if (jug == null) return;
            Crown(jug);
            Announce("canevents:juggernaut-reassigned", runningEvent.NameOf(jug));
        }

        // knownBody: BodyOf no longer returns a dying bot.
        private void RemoveCurrentBuff(Entity knownBody = null)
        {
            if (currentJugUid == null) return;
            RemoveBuff(knownBody ?? runningEvent.BodyOf(currentJugUid));
            currentJugUid = null;
        }

        private void ApplyBuff(Entity body)
        {
            if (body == null) return;
            var hb = body.GetBehavior<EntityBehaviorHealth>();
            hb?.SetMaxHealthModifiers(BuffKey, bonusHealth); // recomputes MaxHealth
            if (hb != null) hb.Health = hb.MaxHealth;
            body.Stats.Set("meleeWeaponsDamage", BuffKey, damageBonus, false);
        }

        private void RemoveBuff(Entity body)
        {
            if (body == null) return;
            body.GetBehavior<EntityBehaviorHealth>()?.SetMaxHealthModifiers(BuffKey, 0f);
            body.Stats.Remove("meleeWeaponsDamage", BuffKey);
        }

        private string PickRandomActive()
        {
            // Dead players stay active here (no elimination), skip them.
            var candidates = runningEvent.ParticipantsStillInEvent
                .Where(uid => runningEvent.BodyOf(uid)?.Alive == true)
                .ToList();
            if (candidates.Count == 0) return null;
            return candidates[runningEvent.sapi.World.Rand.Next(candidates.Count)];
        }
    }
}
