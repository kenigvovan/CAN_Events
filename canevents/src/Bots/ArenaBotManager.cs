using System;
using System.Collections.Generic;
using canevents.src.EventManagers;
using canevents.src.EventPlacesManagers;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace canevents.src.Bots
{
    // Bot bodies of one match. Bot uids are already on the roster (see BotIdentity).
    public class ArenaBotManager
    {
        private static readonly AssetLocation BotEntityCode = new AssetLocation("canevents", "arenabot");

        private readonly RunningEvent runningEvent;
        private readonly Dictionary<string, Entity> bodies = new Dictionary<string, Entity>();

        // Kept after despawn, results and history need them.
        private readonly Dictionary<string, string> names = new Dictionary<string, string>();

        private readonly Log log;

        public ArenaBotManager(RunningEvent runningEvent)
        {
            this.runningEvent = runningEvent;
            log = Log.For(runningEvent?.sapi?.World?.Logger, "bots");
        }

        public IReadOnlyCollection<string> LiveUids => bodies.Keys;

        public bool Any => bodies.Count > 0;

        public Entity EntityOf(string uid)
            => uid != null && bodies.TryGetValue(uid, out var entity) && entity.Alive ? entity : null;

        // Chunk must be loaded first: AddEntityToChunk does nothing for an unloaded chunk, and the bot
        // then never shows up in GetEntitiesAround (can't be hit). False only if the entity type is missing.
        public bool Spawn(string uid, string displayName, BlockPos pos, float maxHealth, float meleeDamage,
                          Action<Entity> onSpawned = null)
        {
            var sapi = runningEvent.sapi;
            if (sapi.World.GetEntityType(BotEntityCode) == null)
            {
                log.Error("Entity type {0} not found; no bots will be spawned.", BotEntityCode);
                return false;
            }

            // Floor: -5/32 truncates to column 0 instead of -1.
            int cx = DimensionChunks.ColumnOf(pos.X);
            int cy = pos.InternalY / GlobalConstants.ChunkSize;   // includes dimension offset
            int cz = DimensionChunks.ColumnOf(pos.Z);

            if (sapi.WorldManager.GetChunk(cx, cy, cz) != null)
            {
                onSpawned?.Invoke(SpawnNow(uid, displayName, pos, maxHealth, meleeDamage));
                return true;
            }

            log.Notification("Chunk {0}/{1}/{2} not loaded, waiting for it before spawning {3}.",
                cx, cy, cz, displayName);
            // Vanilla column load only covers dimension 0.
            DimensionChunks.LoadColumns(sapi, new[] { (cx, cz) }, pos.dimension,
                () =>
                {
                    // Match may be over by now. IsActive alone isn't enough, survivors stay active after the end.
                    if (runningEvent.EventState == EventState.Finished || !runningEvent.IsActive(uid))
                    {
                        log.Notification("{0} no longer in the match, skipping its spawn.", displayName);
                        return;
                    }
                    onSpawned?.Invoke(SpawnNow(uid, displayName, pos, maxHealth, meleeDamage));
                },
                // Only non-zero dimensions time out.
                () => log.Warning("Column {0}/{1} of dimension {2} did not load; {3} is not spawned.",
                    cx, cz, pos.dimension, displayName));
            return true;
        }

        private Entity SpawnNow(string uid, string displayName, BlockPos pos, float maxHealth, float meleeDamage)
        {
            var sapi = runningEvent.sapi;
            var type = sapi.World.GetEntityType(BotEntityCode);
            if (type == null) return null;

            var entity = sapi.World.ClassRegistry.CreateEntity(type);
            entity.Pos.SetPos(pos.X + 0.5, pos.Y, pos.Z + 0.5);
            entity.Pos.Dimension = pos.dimension;
            entity.Pos.SetFrom(entity.Pos);

            // Before SpawnEntity: Initialize runs inside it and AI tasks read these attributes.
            BotMatchLink.Tag(entity, uid, runningEvent.Id, meleeDamage);

            sapi.World.SpawnEntity(entity);

            // Otherwise the engine removes the corpse on the death tick; we despawn it ourselves.
            if (entity is EntityAgent agent) agent.AllowDespawn = false;

            // In case the entity type is overridden by config; inactive entities run no AI.
            entity.AlwaysActive = true;
            entity.State = EnumEntityState.Active;

            entity.GetBehavior<EntityBehaviorNameTag>()?.SetName(displayName);

            // No new tasks during the freeze. Already running tasks are stopped in FreezeAi.
            var taskAi = entity.GetBehavior<EntityBehaviorTaskAI>();
            if (taskAi != null)
                taskAi.TaskManager.OnShouldExecuteTask += _ => !runningEvent.MovementFrozen;

            var health = entity.GetBehavior<EntityBehaviorHealth>();
            if (health != null && maxHealth > 0f)
            {
                health.BaseMaxHealth = maxHealth;
                health.Health = health.MaxHealth;
                health.MarkDirty();

                // Debug log for hit registration.
                health.onDamaged += (dmg, source) =>
                {
                    log.Notification("{0} hit for {1:0.##} by {2} (hp {3:0.#}/{4:0.#})",
                        displayName, dmg, source?.GetCauseEntity()?.GetName() ?? source?.Source.ToString() ?? "?",
                        health.Health, health.MaxHealth);
                    return dmg;
                };
            }
            else if (health == null)
            {
                log.Error("{0} spawned WITHOUT a health behavior - it cannot be hurt.", displayName);
            }

            bodies[uid] = entity;
            names[uid] = displayName;

            var hp = entity.GetBehavior<EntityBehaviorHealth>();
            log.Notification("Spawned {0} ({1}) id={2} at {3} alive={4} hp={5}",
                displayName, uid, entity.EntityId, entity.Pos.AsBlockPos, entity.Alive,
                hp == null ? "NO HEALTH BEHAVIOR" : $"{hp.Health:0.#}/{hp.MaxHealth:0.#}");

            return entity;
        }

        public string NameOf(string uid)
            => uid != null && names.TryGetValue(uid, out var name) ? name : null;

        public void GiveWeapon(string uid, string itemCode)
        {
            if (string.IsNullOrEmpty(itemCode)) return;
            var slot = (EntityOf(uid) as EntityAgent)?.RightHandItemSlot;
            if (slot == null) return;

            var stack = EventManagers.eventBehavior.Shared.ItemLookup.StackOf(runningEvent.sapi.World, itemCode);
            if (stack == null)
            {
                log.Warning("Weapon '{0}' not found, bot stays bare-handed.", itemCode);
                return;
            }

            slot.Itemstack = stack;
            slot.MarkDirty();
        }

        // Must run on any match end, including aborts.
        public void DespawnAll()
        {
            foreach (var entity in bodies.Values)
            {
                if (entity == null || entity.World == null) continue;
                // Not Die(): auto-despawn is off (see Spawn), the corpse would stay forever.
                runningEvent.sapi.World.DespawnEntity(entity, new EntityDespawnData { Reason = EnumDespawnReason.Removed });
            }
            bodies.Clear();
        }

        public void Forget(string uid) => bodies.Remove(uid);
    }
}
