using canevents.src.EventManagers;
using canevents.src.EventManagers.eventBehavior;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace canevents.src.BlockBehaviors
{
    // Stateless, dispatches to the running CaptureTheFlagEB.
    public class BlockBehaviorCtfFlag : BlockBehavior
    {
        public BlockBehaviorCtfFlag(Block block) : base(block) { }

        // Block calls this overload; the shorter one is [Obsolete].
        public override void OnBlockBroken(IWorldAccessor world, BlockPos pos, IPlayer byPlayer,
            float dropQuantityMultiplier, ref EnumHandling handling)
        {
            // Only live flags are unbreakable. Client can't check, so it predicts unbreakable.
            if (world.Side == EnumAppSide.Server && !IsLiveFlag(world, pos)) return;

            handling = EnumHandling.PreventDefault;

            if (world.Side == EnumAppSide.Server && byPlayer is IServerPlayer sp)
            {
                sp.SendMessage(0, Lang.Get("canevents:ctf-flag-unbreakable"),
                    Vintagestory.API.Common.EnumChatType.Notification);
            }
        }

        // Server only.
        private static bool IsLiveFlag(IWorldAccessor world, BlockPos pos)
        {
            var sapi = world.Api as ICoreServerAPI;
            var mgr = sapi?.ModLoader.GetModSystem<canevents>()?.CANEventManager;
            if (mgr == null) return false;

            foreach (var (_, ctf) in mgr.LiveBehaviors<CaptureTheFlagEB>())
                if (ctf.OwnsFlagAt(pos)) return true;
            return false;
        }

        public override void OnBlockExploded(IWorldAccessor world, BlockPos pos, BlockPos explosionCenter,
            EnumBlastType blastType, ref EnumHandling handling)
        {
            if (world.Side == EnumAppSide.Server && !IsLiveFlag(world, pos)) return;
            handling = EnumHandling.PreventDefault;
        }

        public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer,
            BlockSelection blockSel, ref EnumHandling handling)
        {
            // Claim on both sides, or a held item (e.g. sword) eats the right-click on the client.
            handling = EnumHandling.PreventDefault;

            if (world.Side != EnumAppSide.Server) return true;

            var sapi = world.Api as ICoreServerAPI;
            var player = byPlayer as IServerPlayer;
            if (sapi == null || player == null || blockSel?.Position == null) return true;

            var mgr = sapi.ModLoader.GetModSystem<canevents>()?.CANEventManager;
            if (mgr == null) return true;

            foreach (var (re, ctf) in mgr.LiveBehaviors<CaptureTheFlagEB>())
            {
                if (!re.IsActive(player.PlayerUID)) continue;
                if (ctf.TryTakeFlagAt(player, blockSel.Position)) return true;
            }

            sapi.World.Logger.VerboseDebug("[CTF] right-click on flag at {0} matched no running match",
                blockSel.Position);
            return true;
        }
    }
}
