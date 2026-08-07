using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;

namespace canevents.src.Bots
{
    /// <summary>
    /// Vanilla seek task, but restricted to match opponents: vanilla matches on entity code alone,
    /// so an unfiltered bot would chase passers-by or team-mates.
    /// </summary>
    public class AiTaskArenaBotSeek : AiTaskSeekEntity
    {
        public AiTaskArenaBotSeek(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
            : base(entity, taskConfig, aiConfig) { }

        public override bool IsTargetableEntity(Entity e, float range)
            => base.IsTargetableEntity(e, range) && BotMatchLink.IsOpponent(entity, e);
    }
}
