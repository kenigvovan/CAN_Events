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

        public override string Name { get => "ShrinkingSpleef"; set { } }

        public override void Initialize()
        {
            base.Initialize();
            stepMeltDelay   = Config.Float("stepMeltDelay", 1.5f);
            ParsePrefightSeconds();
            Config.WarnUnknown();
        }

        protected override void AfterPlatformPlaced(List<BlockPos> positions)
        {
            if (stepMeltDelay <= 0f) return; // melt disabled, don't waste BEs

            var blockAccessor = runningEvent.sapi.World.BlockAccessor;
            int spawned = 0, verified = 0;
            foreach (var pos in positions)
            {
                blockAccessor.SpawnBlockEntity("SpleefMelt", pos);
                spawned++;
                var check = blockAccessor.GetBlockEntity(pos);
                if (check is BlockEntities.BlockEntitySpleefMelt) verified++;
            }
            log.Notification("Spawned {0} SpleefMelt BEs, {1} verified; sample pos: {2}",
                spawned, verified, positions.Count > 0 ? positions[0].ToString() : "(none)");
        }

        // TPThereAndBack teleports players into the cage; the base runs the caged countdown and
        // calls OnCageOpened when it expires.
        public override void OnStart()
        {
            base.OnStart();   // platform protection
            StartPrefight();
        }

        public override void OnTick(float dt)
        {
            if (TickPrefight(dt)) return;

            base.OnTick(dt);   // step detection runs on a faster separate listener, see OnCageOpened
        }

        private void StepDetectTick(float dt)
        {
            if (PrefightActive) return;
            var blockAccessor = runningEvent.sapi.World.BlockAccessor;
            var sapi = runningEvent.sapi;

            foreach (var plGuid in runningEvent.ParticipantsStillInEvent)
            {
                var player = sapi.World.PlayerByUid(plGuid) as IServerPlayer;
                if (player == null) continue;

                var feet = player.Entity.Pos.AsBlockPos;
                scratchPos.Set(feet.X, feet.Y, feet.Z);
                scratchPos.dimension = feet.dimension;
                var be = blockAccessor.GetBlockEntity(scratchPos) as BlockEntities.BlockEntitySpleefMelt;
                if (be == null)
                {
                    scratchPos.Set(feet.X, feet.Y - 1, feet.Z);
                    scratchPos.dimension = feet.dimension;
                    be = blockAccessor.GetBlockEntity(scratchPos) as BlockEntities.BlockEntitySpleefMelt;
                }
                if (be == null) continue;
                if (!be.IsMelting) be.StartMelt(sapi, stepMeltDelay);
            }
        }

        protected override void OnCageOpened()
        {
            // Fast listener (250 ms) so even brief taps on a block register
            if (stepMeltDelay > 0f && stepDetectListenerId < 0)
            {
                stepDetectListenerId = runningEvent.RegisterMatchTick(StepDetectTick, 250);
            }
        }

        public override void OnEnd()
        {
            if (stepDetectListenerId >= 0)
            {
                runningEvent.sapi.Event.UnregisterGameTickListener(stepDetectListenerId);
                stepDetectListenerId = -1;
            }

            // Base OnEnd removes leftover SpleefMelt BEs before restoring the platform.
            base.OnEnd();
        }

        // No Clone() override: Initialize() re-reads every field from Attributes, and RunningEvent
        // always calls it after the base Activator clone.
    }
}
