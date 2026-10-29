using canevents.src.EventManagers;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace canevents.src.Bots
{
    // Bots are saved with chunks (see EntityArenaBot), so orphans from a crash are removed on load.
    public class BotCleanup
    {
        private readonly ICoreServerAPI sapi;
        private readonly CANEventManager manager;
        private readonly EntityDelegate onEntityLoaded;

        public BotCleanup(ICoreServerAPI sapi, CANEventManager manager)
        {
            this.sapi = sapi;
            this.manager = manager;
            onEntityLoaded = OnEntityLoaded;
            sapi.Event.OnEntityLoaded += onEntityLoaded;
        }

        public void Dispose() => sapi.Event.OnEntityLoaded -= onEntityLoaded;

        private void OnEntityLoaded(Entity entity)
        {
            if (entity is not EntityArenaBot) return;

            string uid = BotMatchLink.UidOf(entity);
            var match = BotMatchLink.MatchOf(entity);

            if (uid != null && match != null && match.IsActive(uid)) return;

            sapi.World.Logger.Notification(
                "[bots] Removing orphaned bot body {0} at {1} (no live match).", uid ?? "?", entity.Pos.AsBlockPos);
            // Not Die(): AllowDespawn is false (ArenaBotManager.Spawn).
            sapi.World.DespawnEntity(entity, new EntityDespawnData { Reason = EnumDespawnReason.Removed });
        }
    }
}
