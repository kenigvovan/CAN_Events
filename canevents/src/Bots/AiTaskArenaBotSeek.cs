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

        // entityCodes only lists players and bots; match mobs can be any creature.
        public override bool IsTargetableEntity(Entity e, float range)
            => (Mobs.MatchMobLink.IsMatchMob(e) ? e.Alive && CanSense(e, range) : base.IsTargetableEntity(e, range))
               && BotMatchLink.IsOpponent(entity, e);
    }
}
