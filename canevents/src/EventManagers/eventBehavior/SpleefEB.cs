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

        public override string Name { get => "Spleef"; set { } }

        public override void Initialize()
        {
            base.Initialize();
            toolCode        = Config.String("toolCode", "game:shovel-iron");
            meltDelay       = Config.Float("meltDelay", 0f);
            hitMeltDelay    = Config.Float("hitMeltDelay", 0f);
            ParsePrefightSeconds();
            Config.WarnUnknown();
        }

        protected override void AfterPlatformPlaced(List<BlockPos> positions)
        {
            // Both timers need the BE: the break handler PreventDefaults every dig on a spleef block,
            // so without a BE to melt it a hitMeltDelay-only config left the platform unbreakable.
            // With only hitMeltDelay the BEs spawn idle and start counting on the first hit.
            if (meltDelay > 0f || hitMeltDelay > 0f)
                SpawnMeltEntities(positions, startMelting: meltDelay > 0f);
        }

        private void SpawnMeltEntities(IEnumerable<BlockPos> positions, bool startMelting)
        {
            var blockAccessor = runningEvent.sapi.World.BlockAccessor;
            foreach (var pos in positions)
            {
                blockAccessor.SpawnBlockEntity("SpleefMelt", pos);
                if (!startMelting) continue;
                var entity = blockAccessor.GetBlockEntity(pos) as BlockEntities.BlockEntitySpleefMelt;
                entity?.StartMelt(runningEvent.sapi, meltDelay);
            }
        }

        // The base already protects the block for everyone; this is only what a hit MEANS here.
        protected override void OnPlatformBlockHit(IServerPlayer player, BlockPos pos)
        {
            if (hitMeltDelay <= 0f) return;
            var entity = runningEvent.sapi.World.BlockAccessor.GetBlockEntity(pos) as BlockEntities.BlockEntitySpleefMelt;
            if (entity == null) return;
            // Already counting down: pull the deadline in. Otherwise start fresh - this is the only
            // melt trigger in a hitMeltDelay-only config.
            if (entity.IsMelting) entity.ReduceDelay(hitMeltDelay);
            else entity.StartMelt(runningEvent.sapi, hitMeltDelay);
        }

        // TPThereAndBack teleports players into the cage earlier in OnStart; the base runs the
        // caged countdown and calls OnCageOpened when it expires.
        public override void OnStart()
        {
            base.OnStart();   // platform protection
            StartPrefight();
        }

        public override void OnTick(float dt)
        {
            if (TickPrefight(dt)) return;   // suppresses fall checks while caged

            base.OnTick(dt);
        }

        protected override void OnCageOpened()
        {
            foreach (var plGuid in runningEvent.ParticipantsBefore)
            {
                // Only who is actually in play: a pre-start dropout is never teleported in, so a
                // shovel handed to them would be minted outside the match.
                if (!runningEvent.IsActive(plGuid)) continue;
                var player = runningEvent.sapi.World.PlayerByUid(plGuid) as IServerPlayer;
                if (player == null) continue;
                var shovel = runningEvent.sapi.World.GetItem(new AssetLocation(toolCode));
                if (shovel != null)
                    InventoryUtil.Give(runningEvent.sapi, player, new ItemStack(shovel, 1), "spleef-tool");
            }
        }

        // No Clone() override: Initialize() re-reads every field from Attributes, and RunningEvent
        // always calls it after the base Activator clone.
    }
}
