using canevents.src.EventPlacesManagers.Places;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace canevents.src.EventManagers.eventBehavior.Shared
{
    /// <summary>
    /// Takes issued kit left lying on an arena away when the match ends. Without it a thrown sword
    /// simply stays there: the match is over, the player's inventory has been restored, and the
    /// item belongs to nobody - free for whoever walks past next, including the player who dropped
    /// it on purpose.
    /// </summary>
    public static class IssuedGearSweep
    {
        /// <summary>
        /// A thrown item bounces, and arena bounds are marked by walking the corners rather than
        /// measured, so the edge is approximate. Only marked items are ever taken, which is what
        /// makes a margin safe here.
        /// </summary>
        private const int HorizontalMargin = 3;

        /// <summary>Removes every issued item lying on this place; returns how many went.</summary>
        public static int Sweep(ICoreServerAPI sapi, EventPlace place, Log log)
        {
            // No bounds, no sweep. The alternative - guessing a radius around the spawn points -
            // would reach into whatever happens to stand next to an unmarked arena, and taking
            // items out of someone's base is a far worse bug than a sword left on the sand.
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
                // DespawnEntity, not Die(): a dying item entity still leaves its stack behind for
                // the world to keep - the same reason the bot sweep uses it.
                sapi.World.DespawnEntity(entity,
                    new EntityDespawnData { Reason = EnumDespawnReason.Removed });
            }

            log?.Notification("Swept {0} issued item(s) off '{1}'.", found.Length, place.Name ?? "?");
            return found.Length;
        }
    }
}
