using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;

namespace canevents.src.Bots
{
    // Vanilla seek limited to match opponents.
    public class AiTaskArenaBotSeek : AiTaskSeekEntity
    {
        public AiTaskArenaBotSeek(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
            : base(entity, taskConfig, aiConfig) { }

        public override bool IsTargetableEntity(Entity e, float range)
            => base.IsTargetableEntity(e, range) && BotMatchLink.IsOpponent(entity, e);
    }
}
