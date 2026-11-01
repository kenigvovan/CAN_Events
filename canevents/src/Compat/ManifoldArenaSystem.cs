using System;
using canevents.src.EventPlacesManagers;
using Manifold.Api.Helpers;
using Manifold.Api.Server;
using Manifold.Api.Transitions;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace canevents.src.Compat
{
    // Runs only with Manifold. Its types may appear in method bodies only: VS scans canevents.dll with
    // GetTypes(), and a Manifold field, base type or interface would make it drop the whole assembly
    // when Manifold is missing. That is also why there is no own IWorldgenStrategy: the dimension is
    // void and ArenaPackService lays the floor.
    public class ManifoldArenaSystem : ModSystem, IArenaDimensionHost
    {
        public const string ModId = "manifold";

        private static readonly AssetLocation ArenaDim = new AssetLocation("canevents", "arenas");
        private static readonly AssetLocation Overworld = new AssetLocation("manifold", "overworld");

        private ICoreServerAPI sapi;

        public override double ExecuteOrder() => 0.35;

        public override bool ShouldLoad(ICoreAPI api)
            => api.Side == EnumAppSide.Server && api.ModLoader.IsModEnabled(ModId);

        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;
            try
            {
                api.GetManifoldServer(this).Registry.Define(ArenaDim)
                    .WithWorldgen(new BasicVoidWorldgenStrategy())
                    .Persistent()
                    .WithGenerationRadius(5)
                    .RegisterStatic();
                ArenaPackService.Host = this;
                Mod.Logger.Notification("Arena dimension {0} is dim {1}.", ArenaDim, DimId);
            }
            catch (Exception e)
            {
                Mod.Logger.Error("Could not register the arena dimension with Manifold, arena packs go to the overworld: {0}", e);
            }
        }

        public int DimId => sapi.GetManifoldServer(this).Registry.Get(ArenaDim)?.InternalId ?? 0;

        public void Send(IServerPlayer player, int x, int y, int z)
        {
            sapi.GetManifoldServer(this).Transitions.TeleportPlayer(player, ArenaDim,
                new TransitionOptions { OverridePosition = new BlockPos(x, y, z) });
        }

        public void SendBack(IServerPlayer player)
        {
            sapi.GetManifoldServer(this).Transitions.TeleportPlayer(player, Overworld);
        }
    }
}
