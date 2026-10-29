using canevents.src.EventPlacesManagers.Places;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace canevents.src.EventManagers.eventBehavior.Shared
{
    // Removes issued kit left lying on the arena after a match.
    public static class IssuedGearSweep
    {
        // Bounds are approximate; only marked items are taken, so a margin is safe.
        private const int HorizontalMargin = 3;

        public static int Sweep(ICoreServerAPI sapi, EventPlace place, Log log)
        {
            // No bounds, no sweep: a guessed radius could reach into a neighbour's base.
            if (sapi == null
                || !PlaceBounds.TryCuboid(place, HorizontalMargin,
                       sapi.World.BlockAccessor.MapSizeY, out var from, out var to))
            {
                log?.Debug("Issued-gear sweep skipped for '{0}': the place has no marked bounds.",
                    place?.Name ?? "?");
                return 0;
            }

            var found = sapi.World.GetEntitiesInsideCuboid(from, to,
                e => e is EntityItem item && IssuedGear.IsIssued(item.Itemstack));
            if (found == null || found.Length == 0) return 0;

            foreach (var entity in found)
            {
                // DespawnEntity, not Die(): a dying item entity leaves its stack behind.
                sapi.World.DespawnEntity(entity,
                    new EntityDespawnData { Reason = EnumDespawnReason.Removed });
            }

            log?.Notification("Swept {0} issued item(s) off '{1}'.", found.Length, place.Name ?? "?");
            return found.Length;
        }
    }
}
