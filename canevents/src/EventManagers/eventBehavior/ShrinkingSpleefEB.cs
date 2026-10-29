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
    public class ShrinkingSpleefEB : SpleefBaseEB
    {
        private float stepMeltDelay = 1.5f;
        private long stepDetectListenerId = -1;
        private readonly BlockPos scratchPos = new BlockPos();

        public ShrinkingSpleefEB(string name, JsonObject attributes, RunningEvent runningEvent)
            : base(name, attributes, runningEvent) { }

        public override string Name => BehaviorNames.ShrinkingSpleef;

        protected override void ReadModeConfig()
            => stepMeltDelay = Config.Float("stepMeltDelay", 1.5f);

        protected override void AfterPlatformPlaced(List<BlockPos> positions)
        {
            if (stepMeltDelay <= 0f) return;
            SpawnMeltEntities(positions, startDelay: null); // idle until stepped on
        }

        private void StepDetectTick(float dt)
        {
            if (PrefightActive) return;

            foreach (var plGuid in runningEvent.ParticipantsStillInEvent)
            {
                // BodyOf so bots count too.
                var body = runningEvent.BodyOf(plGuid);
                if (body == null) continue;

                var feet = body.Pos.AsBlockPos;
                var be = MeltAt(feet, 0) ?? MeltAt(feet, -1);
                if (be != null && !be.IsMelting) be.StartMelt(runningEvent.sapi, stepMeltDelay);
            }
        }

        private BlockEntities.BlockEntitySpleefMelt MeltAt(BlockPos feet, int dy)
        {
            scratchPos.Set(feet.X, feet.Y + dy, feet.Z);
            scratchPos.dimension = feet.dimension;
            return runningEvent.sapi.World.BlockAccessor.GetBlockEntity(scratchPos) as BlockEntities.BlockEntitySpleefMelt;
        }

        protected override void OnCageOpened()
        {
            // 250 ms so brief steps still register.
            if (stepMeltDelay > 0f && stepDetectListenerId < 0)
            {
                stepDetectListenerId = runningEvent.RegisterMatchTick(StepDetectTick, 250);
            }
        }

        public override void OnEnd()
        {
            runningEvent.UnregisterMatchTick(stepDetectListenerId);
            stepDetectListenerId = -1;

            base.OnEnd();
        }

        // No Clone() override: RunningEvent calls Initialize() after cloning, which re-reads Attributes.
    }
}
