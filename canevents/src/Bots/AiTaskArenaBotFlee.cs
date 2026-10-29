using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;

namespace canevents.src.Bots
{
    // Vanilla flee filtered by the mode's roles (e.g. run from the murderer in Murder Mystery).
    public class AiTaskArenaBotFlee : AiTaskFleeEntity
    {
        public AiTaskArenaBotFlee(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
            : base(entity, taskConfig, aiConfig) { }

        public override bool IsTargetableEntity(Entity e, float range)
            => base.IsTargetableEntity(e, range) && BotMatchLink.ShouldFlee(entity, e);

        public override bool ShouldExecute()
        {
            if (!base.ShouldExecute()) return false;

            // Vanilla can also pick a target on being hit, bypassing IsTargetableEntity. Recheck it.
            return BotMatchLink.ShouldFlee(entity, targetEntity);
        }
    }
}
