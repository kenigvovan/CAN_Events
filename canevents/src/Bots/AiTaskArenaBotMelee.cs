using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;

namespace canevents.src.Bots
{
    /// <summary>
    /// Melee task that only targets opponents in the same match, and scales damage with the held
    /// weapon (vanilla reads one fixed number from JSON). Base damage rides on the entity so each
    /// event can field tougher bots without a second entity type.
    /// </summary>
    public class AiTaskArenaBotMelee : AiTaskMeleeAttack
    {
        private readonly float configuredDamage;
        private readonly float weaponDamageMultiplier;

        public AiTaskArenaBotMelee(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
            : base(entity, taskConfig, aiConfig)
        {
            configuredDamage = damage;
            weaponDamageMultiplier = taskConfig["weaponDamageMultiplier"].AsFloat(1f);

            // Vanilla hardcodes the search radius; a bot already up against its opponent needs a
            // more forgiving one.
            attackRange = taskConfig["attackRange"].AsFloat(attackRange);
        }

        public override bool IsTargetableEntity(Entity e, float range)
            => base.IsTargetableEntity(e, range) && BotMatchLink.IsOpponent(entity, e);

        public override void StartExecute()
        {
            damage = ResolveDamage();
            base.StartExecute();
        }

        private float ResolveDamage()
        {
            // Bare-handed damage: whatever this event set for its bots, else the entity's own default.
            float unarmed = entity.WatchedAttributes.GetFloat(BotMatchLink.DamageAttribute, configuredDamage);

            var held = entity.RightHandItemSlot?.Itemstack?.Item;
            if (held == null) return unarmed;

            float weapon = held.AttackPower * weaponDamageMultiplier;
            return weapon > unarmed ? weapon : unarmed;
        }
    }
}
