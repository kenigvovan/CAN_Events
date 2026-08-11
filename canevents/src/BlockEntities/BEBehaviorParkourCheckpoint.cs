using System.Linq;
using canevents.src.EventManagers;
using canevents.src.EventManagers.eventBehavior;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace canevents.src.BlockEntities
{
    /// <summary>
    /// Polls for participants of an active parkour event standing on this block and dispatches to
    /// that event's ParkourEB. Detection is by proximity, not collision, so any block can be a marker.
    /// </summary>
    public class BEBehaviorParkourCheckpoint : TickingBEBehavior
    {
        private const int CheckIntervalMs = 200;

        public BEBehaviorParkourCheckpoint(BlockEntity be) : base(be) { }

        public void Start()
        {
            if (!StartListener(CheckIntervalMs))
                Api?.Logger?.Warning("[ParkourCP] Start called at {0} but Api is not server (Api={1})",
                    Blockentity?.Pos, Api?.GetType().Name ?? "null");
        }

        public void Stop() => StopListener();

        protected override void OnTick(float dt)
        {
            var sapi = ServerApi;
            if (sapi == null) return;
            var modSys = sapi.ModLoader.GetModSystem<canevents>();
            var mgr = modSys?.CANEventManager;
            if (mgr == null) return;

            var be = Blockentity as BlockEntityParkourCheckpoint;
            if (be == null) return;

            var pos = Blockentity.Pos;

            foreach (var re in mgr.RunningEventsSnapshot())
            {
                if (re.EventState != EventState.Running) continue;
                var eb = re.GetBehavior<ParkourEB>();
                if (eb == null) continue;

                // Copy: the finish checkpoint can finalize the race, eliminating players out of
                // the set being iterated.
                foreach (var uid in re.ParticipantsStillInEvent.ToArray())
                {
                    var player = sapi.World.PlayerByUid(uid) as IServerPlayer;
                    if (player == null) continue;
                    var pBlock = player.Entity.Pos.AsBlockPos;
                    // The checkpoint is the floor block, so the foot block must be directly above it.
                    if (pBlock.X == pos.X && pBlock.Z == pos.Z && pBlock.Y == pos.Y + 1)
                    {
                        eb.OnPlayerHitCheckpoint(player, pos, be.Index, be.IsFinish);
                    }
                }
            }
        }
    }
}
