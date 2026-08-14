using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;

namespace canevents.src.Bots
{
    /// <summary>
    /// Flee task for bots that are not allowed to fight back. Vanilla flees from an entity code, so
    /// an unfiltered version would have the townsfolk scattering from each other; this one runs only
    /// from someone the mode says is a threat to this bot - in Murder Mystery, an armed murderer.
    ///
    /// Without it an unarmed bot just stands there while it is cut down, which reads as a broken bot
    /// rather than as a victim.
    /// </summary>
    public class AiTaskArenaBotFlee : AiTaskFleeEntity
    {
        public AiTaskArenaBotFlee(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
            : base(entity, taskConfig, aiConfig) { }

        public override bool IsTargetableEntity(Entity e, float range)
            => base.IsTargetableEntity(e, range) && BotMatchLink.ShouldFlee(entity, e);

        public override bool ShouldExecute()
        {
            if (!base.ShouldExecute()) return false;

            // The vanilla task also starts for reasons of its own - being hit is the usual one - and
            // those paths pick a target without going through the filter above. That had the
            // murderer running from the victim it had just stabbed, so the chosen target is checked
            // once more here, where every path has already converged.
            return BotMatchLink.ShouldFlee(entity, targetEntity);
        }
    }
}
