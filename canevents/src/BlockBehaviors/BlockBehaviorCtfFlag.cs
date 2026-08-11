using canevents.src.EventManagers;
using canevents.src.EventManagers.eventBehavior;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace canevents.src.BlockBehaviors
{
    /// <summary>
    /// Right-click handling for a CTF flag block: taking a flag is deliberate, unlike the proximity
    /// pickup it replaces. Stateless - it dispatches to the running match's CaptureTheFlagEB, so a
    /// flag left by a crashed match simply does nothing.
    /// </summary>
    public class BlockBehaviorCtfFlag : BlockBehavior
    {
        public BlockBehaviorCtfFlag(Block block) : base(block) { }

        /// <summary>
        /// Flags cannot be mined: breaking one while the match still counts it as home leaves it
        /// neither stealable nor returnable, so that team can never be scored against.
        /// </summary>
        /// This overload is the one Block actually calls; the shorter one is [Obsolete].
        public override void OnBlockBroken(IWorldAccessor world, BlockPos pos, IPlayer byPlayer,
            float dropQuantityMultiplier, ref EnumHandling handling)
        {
            // Unbreakable only while a live match owns the flag; orphan debris stays mineable, since
            // nothing sweeps it at boot. The client can't inspect server state, so it predicts unbreakable.
            if (world.Side == EnumAppSide.Server && !IsLiveFlag(world, pos)) return;

            handling = EnumHandling.PreventDefault;

            if (world.Side == EnumAppSide.Server && byPlayer is IServerPlayer sp)
            {
                sp.SendMessage(0, Lang.Get("canevents:ctf-flag-unbreakable"),
                    Vintagestory.API.Common.EnumChatType.Notification);
            }
        }

        // True while a running CTF match still counts a flag here. Server-only: the client has no
        // CANEventManager.
        private static bool IsLiveFlag(IWorldAccessor world, BlockPos pos)
        {
            var sapi = world.Api as ICoreServerAPI;
            var mgr = sapi?.ModLoader.GetModSystem<canevents>()?.CANEventManager;
            if (mgr == null) return false;

            foreach (var re in mgr.RunningEventsSnapshot())
            {
                if (re.EventState != EventState.Running) continue;
                for (int i = 0; i < re.Behaviors.Count; i++)
                    if (re.Behaviors[i] is CaptureTheFlagEB ctf && ctf.OwnsFlagAt(pos)) return true;
            }
            return false;
        }

        /// <summary>As OnBlockBroken, for the path with no player behind it: arenas do hand out
        /// explosives, and a blast near a base would otherwise delete a live flag.</summary>
        public override void OnBlockExploded(IWorldAccessor world, BlockPos pos, BlockPos explosionCenter,
            EnumBlastType blastType, ref EnumHandling handling)
        {
            // As OnBlockBroken: protect a live flag, but let an orphaned one be destroyed normally.
            if (world.Side == EnumAppSide.Server && !IsLiveFlag(world, pos)) return;
            handling = EnumHandling.PreventDefault;
        }

        public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer,
            BlockSelection blockSel, ref EnumHandling handling)
        {
            // Client side predicts nothing here: the authority on who holds a flag is the server,
            // and a mispredicted pickup would show a flag the server never handed over.
            // Claim the interaction on BOTH sides. Returning PassThrough on the client lets the
            // held item act instead (swinging a sword eats the right-click), so the server hook
            // never fires and the flag looks unresponsive.
            handling = EnumHandling.PreventDefault;

            if (world.Side != EnumAppSide.Server) return true;

            var sapi = world.Api as ICoreServerAPI;
            var player = byPlayer as IServerPlayer;
            if (sapi == null || player == null || blockSel?.Position == null) return true;

            var mgr = sapi.ModLoader.GetModSystem<canevents>()?.CANEventManager;
            if (mgr == null) return true;

            foreach (var re in mgr.RunningEventsSnapshot())
            {
                if (re.EventState != EventState.Running) continue;
                if (!re.IsActive(player.PlayerUID)) continue;

                for (int i = 0; i < re.Behaviors.Count; i++)
                {
                    if (!(re.Behaviors[i] is CaptureTheFlagEB ctf)) continue;
                    if (ctf.TryTakeFlagAt(player, blockSel.Position)) return true;
                }
            }

            // Nothing claimed it: a flag block with no live match behind it. Say so rather than
            // leaving the player clicking a prop in silence.
            sapi.World.Logger.VerboseDebug("[CTF] right-click on flag at {0} matched no running match",
                blockSel.Position);
            return true;
        }
    }
}
