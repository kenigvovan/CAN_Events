using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;

namespace canevents.src.Bots
{
    // Targets only match opponents; damage uses the held weapon (vanilla uses a fixed JSON value).
    public class AiTaskArenaBotMelee : AiTaskMeleeAttack
    {
        private readonly float configuredDamage;
        private readonly float weaponDamageMultiplier;

        public AiTaskArenaBotMelee(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
            : base(entity, taskConfig, aiConfig)
        {
            configuredDamage = damage;
            weaponDamageMultiplier = taskConfig["weaponDamageMultiplier"].AsFloat(1f);

            // Vanilla hardcodes it, too short for bots.
            attackRange = taskConfig["attackRange"].AsFloat(attackRange);
        }

        public override bool IsTargetableEntity(Entity e, float range)
            => (Mobs.MatchMobLink.IsMatchMob(e) ? e.Alive && CanSense(e, range) : base.IsTargetableEntity(e, range))
               && BotMatchLink.IsOpponent(entity, e);

        public override void StartExecute()
        {
            damage = ResolveDamage();
            base.StartExecute();
        }

        // Hit-and-run profile: the movement task backs off for a moment after a landed hit.
        protected override void attackTarget()
        {
            bool wasAlive = targetEntity?.Alive == true;
            base.attackTarget();
            var profile = Mobs.Ai.RaiderProfiles.Of(entity);
            if (wasAlive && profile.HitAndRunSeconds > 0f)
                entity.Attributes.SetLong(Mobs.Ai.RaiderProfiles.RetreatUntilAttribute,
                    world.ElapsedMilliseconds + (long)(profile.HitAndRunSeconds * 1000));
        }

        private float ResolveDamage()
        {
            float unarmed = entity.WatchedAttributes.GetFloat(BotMatchLink.DamageAttribute, configuredDamage);

            // Same stat as player melee, so mode buffs (Juggernaut) apply to bots.
            float statMul = entity.Stats.GetBlended("meleeWeaponsDamage");

            var held = entity.RightHandItemSlot?.Itemstack?.Item;
            if (held == null) return unarmed * statMul;

            float weapon = held.AttackPower * weaponDamageMultiplier;
            return (weapon > unarmed ? weapon : unarmed) * statMul;
        }
    }
}
