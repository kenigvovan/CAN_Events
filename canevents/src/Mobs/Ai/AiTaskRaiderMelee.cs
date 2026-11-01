using canevents.src.Bots;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;

namespace canevents.src.Mobs.Ai
{
    // Vanilla melee swing on the director's target only, weapon damage like the bots'.
    // A hit-and-run profile sets a retreat window that AiTaskRaiderEngage honours.
    public class AiTaskRaiderMelee : AiTaskMeleeAttack
    {
        private readonly float weaponDamageMultiplier;

        public AiTaskRaiderMelee(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
            : base(entity, taskConfig, aiConfig)
        {
            weaponDamageMultiplier = taskConfig["weaponDamageMultiplier"].AsFloat(1f);
            attackRange = taskConfig["attackRange"].AsFloat(attackRange);
        }

        // Anyone pressing up close counts too, the same as the director lets vanilla mobs.
        public override bool IsTargetableEntity(Entity e, float range)
            => e != null && e.Alive && (e == RaiderSenses.TargetOf(entity) || MatchMobLink.MayTarget(entity, e));

        public override void StartExecute()
        {
            damage = ResolveDamage();
            base.StartExecute();
        }

        protected override void attackTarget()
        {
            bool wasAlive = targetEntity?.Alive == true;
            base.attackTarget();
            if (!wasAlive) return;

            var profile = RaiderProfiles.Of(entity);
            if (profile.HitAndRunSeconds > 0f)
                entity.Attributes.SetLong(RaiderProfiles.RetreatUntilAttribute,
                    world.ElapsedMilliseconds + (long)(profile.HitAndRunSeconds * 1000));
        }

        private float ResolveDamage()
        {
            float unarmed = entity.WatchedAttributes.GetFloat(BotMatchLink.DamageAttribute, damage);
            var held = entity.RightHandItemSlot?.Itemstack?.Item;
            // A bow is no club: an archer caught up close hits with its fists.
            if (held == null || RaiderProfiles.Of(entity).Ranged) return unarmed;
            float weapon = held.AttackPower * weaponDamageMultiplier;
            return weapon > unarmed ? weapon : unarmed;
        }
    }
}
