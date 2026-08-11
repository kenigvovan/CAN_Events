using canevents.src.EventManagers;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace canevents.src.Bots
{
    /// <summary>
    /// Removes bot bodies that no longer belong to a live match - a crash or an unloaded chunk can
    /// leave one behind. Bodies are stored with their chunk (they must be, or the server drops
    /// bodies the client still renders), so the sweep runs when a chunk brings one back.
    /// </summary>
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
            // DespawnEntity, not Die(): bots are spawned with AllowDespawn = false (see
            // ArenaBotManager.Spawn), so Die() would leave the corpse standing here for good - which is
            // exactly what this sweep exists to clear.
            sapi.World.DespawnEntity(entity, new EntityDespawnData { Reason = EnumDespawnReason.Removed });
        }
    }
}
