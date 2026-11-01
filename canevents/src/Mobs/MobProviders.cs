using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace canevents.src.Mobs
{
    // Where a match mob comes from. Modes only ever see this interface, never a mod's types:
    // the mod must work without any of the optional mob mods installed.
    public interface IMobProvider
    {
        string Id { get; }

        bool Handles(MobEntry entry);

        // null = can spawn here
        string Unavailable(ICoreServerAPI sapi, MobEntry entry);

        // Feet on top of pos. beforeSpawn runs before the entity enters the world when the provider allows it, else right after.
        // A group line may bring several bodies.
        List<Entity> Spawn(ICoreServerAPI sapi, MobEntry entry, BlockPos pos, Action<Entity> beforeSpawn);
    }

    public sealed class VanillaMobProvider : IMobProvider
    {
        public string Id => "entity";

        public bool Handles(MobEntry entry) => !string.IsNullOrEmpty(entry.Code);

        public string Unavailable(ICoreServerAPI sapi, MobEntry entry)
            => sapi.World.GetEntityType(new AssetLocation(entry.Code)) == null
                ? $"entity type '{entry.Code}' does not exist"
                : null;

        public List<Entity> Spawn(ICoreServerAPI sapi, MobEntry entry, BlockPos pos, Action<Entity> beforeSpawn)
        {
            var type = sapi.World.GetEntityType(new AssetLocation(entry.Code));
            if (type == null) return new List<Entity>();

            var entity = sapi.World.ClassRegistry.CreateEntity(type);
            entity.Pos.SetPos(pos.X + 0.5, pos.Y, pos.Z + 0.5);
            entity.Pos.Dimension = pos.dimension;
            entity.Pos.Yaw = (float)(sapi.World.Rand.NextDouble() * GameMath.TWOPI);
            entity.Pos.SetFrom(entity.Pos);
            beforeSpawn?.Invoke(entity);
            sapi.World.SpawnEntity(entity);
            return entity.EntityId == 0 ? new List<Entity>() : new List<Entity> { entity };
        }
    }

    // Entries no provider can serve are dropped at load, a warning each, the rest of the wave still spawns.
    public static class MobProviders
    {
        private static readonly List<IMobProvider> providers = new List<IMobProvider> { new VanillaMobProvider() };

        public static void Register(IMobProvider provider)
        {
            if (provider == null || providers.Any(p => p.Id == provider.Id)) return;
            providers.Insert(0, provider);
        }

        public static IMobProvider For(MobEntry entry) => providers.FirstOrDefault(p => p.Handles(entry));
    }
}
