using System;
using System.Collections.Generic;
using canevents.src.EventManagers;
using canevents.src.EventManagers.events;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace canevents.src.EventManagers.eventBehavior
{
    public class SpleefEB : SpleefBaseEB
    {
        private string toolCode = "game:shovel-iron";
        private float meltDelay = 0f;
        private float hitMeltDelay = 0f;

        public SpleefEB(string name, JsonObject attributes, RunningEvent runningEvent)
            : base(name, attributes, runningEvent) { }

        public override string Name => BehaviorNames.Spleef;

        protected override void ReadModeConfig()
        {
            toolCode        = Config.String("toolCode", "game:shovel-iron");
            meltDelay       = Config.Float("meltDelay", 0f);
            hitMeltDelay    = Config.Float("hitMeltDelay", 0f);
        }

        protected override void AfterPlatformPlaced(List<BlockPos> positions)
        {
            // Break handler cancels digs on spleef blocks, so hitMeltDelay alone also needs the BE (idle until hit).
            if (meltDelay > 0f || hitMeltDelay > 0f)
                SpawnMeltEntities(positions, meltDelay > 0f ? meltDelay : (float?)null);
        }

        protected override bool OnPlatformBlockHit(IServerPlayer player, BlockPos pos)
        {
            // No hit timer: the dig breaks the block.
            if (hitMeltDelay <= 0f) return false;

            var entity = runningEvent.sapi.World.BlockAccessor.GetBlockEntity(pos) as BlockEntities.BlockEntitySpleefMelt;
            if (entity == null) return true;
            if (entity.IsMelting) entity.ReduceDelay(hitMeltDelay);
            else entity.StartMelt(runningEvent.sapi, hitMeltDelay);
            return true;
        }

        protected override void OnCageOpened()
        {
            // Own gear is not restored after, so a handed-out shovel would leave the arena.
            if (runningEvent.KeepOwnGear)
            {
                log.Notification("KeepOwnGear: no spleef tool handed out.");
                return;
            }

            var shovel = runningEvent.sapi.World.GetItem(new AssetLocation(toolCode));
            if (shovel == null) return;

            // Pre-start dropouts were never teleported in.
            foreach (var player in runningEvent.ActiveOnlineParticipants)
            {
                var stack = new ItemStack(shovel, 1);
                Shared.IssuedGear.Mark(stack, player.PlayerName);
                InventoryUtil.Give(runningEvent.sapi, player, stack, "spleef-tool");
            }
        }

        // No Clone() override: RunningEvent calls Initialize() after cloning, which re-reads Attributes.
    }
}
