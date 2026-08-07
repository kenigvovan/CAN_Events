using System;
using System.Collections.Generic;
using canevents.src.EventManagers;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace canevents.src.Bots
{
    /// <summary>
    /// The bot bodies of one match: spawn, gear, despawn. Owns only the physical side - the roster
    /// already holds bot uids from lobby fill (see <see cref="BotIdentity"/>).
    /// </summary>
    public class ArenaBotManager
    {
        private static readonly AssetLocation BotEntityCode = new AssetLocation("canevents", "arenabot");

        private readonly RunningEvent runningEvent;
        private readonly Dictionary<string, Entity> bodies = new Dictionary<string, Entity>();

        // Names outlive bodies on purpose: results and match history are written after despawn.
        private readonly Dictionary<string, string> names = new Dictionary<string, string>();

        private readonly Log log;

        public ArenaBotManager(RunningEvent runningEvent)
        {
            this.runningEvent = runningEvent;
            log = Log.For(runningEvent?.sapi?.World?.Logger, "bots");
        }

        /// <summary>Uids of every bot that has a body right now.</summary>
        public IReadOnlyCollection<string> LiveUids => bodies.Keys;

        public bool Any => bodies.Count > 0;

        /// <summary>The body behind a bot uid, or null for a player uid / a despawned bot.</summary>
        public Entity EntityOf(string uid)
            => uid != null && bodies.TryGetValue(uid, out var entity) && entity.Alive ? entity : null;

        /// <summary>
        /// Spawns a body for a uid, loading the chunk first if needed. That load is mandatory:
        /// SpawnEntity's AddEntityToChunk does nothing for an unloaded chunk and the once-a-second
        /// repair pass only re-files entities whose chunk index changed, so such a bot ticks and
        /// renders but is absent from lookups (GetEntitiesAround) and can never be hit.
        /// onSpawned runs once the body exists; returns false only if the entity type is missing.
        /// </summary>
        public bool Spawn(string uid, string displayName, BlockPos pos, float maxHealth, float meleeDamage,
                          Action<Entity> onSpawned = null)
        {
            var sapi = runningEvent.sapi;
            if (sapi.World.GetEntityType(BotEntityCode) == null)
            {
                log.Error("Entity type {0} not found; no bots will be spawned.", BotEntityCode);
                return false;
            }

            int cx = pos.X / GlobalConstants.ChunkSize;
            int cy = pos.Y / GlobalConstants.ChunkSize;
            int cz = pos.Z / GlobalConstants.ChunkSize;

            if (sapi.WorldManager.GetChunk(cx, cy, cz) != null)
            {
                onSpawned?.Invoke(SpawnNow(uid, displayName, pos, maxHealth, meleeDamage));
                return true;
            }

            log.Notification("Chunk {0}/{1}/{2} not loaded, waiting for it before spawning {3}.",
                cx, cy, cz, displayName);
            sapi.WorldManager.LoadChunkColumnPriority(cx, cz, new ChunkLoadOptions
            {
                OnLoaded = () =>
                {
                    // The match can be over by the time the column is ready; spawning then would
                    // leave a body nobody despawns.
                    if (!runningEvent.IsActive(uid))
                    {
                        log.Notification("{0} no longer in the match, skipping its spawn.", displayName);
                        return;
                    }
                    onSpawned?.Invoke(SpawnNow(uid, displayName, pos, maxHealth, meleeDamage));
                }
            });
            return true;
        }

        private Entity SpawnNow(string uid, string displayName, BlockPos pos, float maxHealth, float meleeDamage)
        {
            var sapi = runningEvent.sapi;
            var type = sapi.World.GetEntityType(BotEntityCode);
            if (type == null) return null;

            var entity = sapi.World.ClassRegistry.CreateEntity(type);
            entity.Pos.SetPos(pos.X + 0.5, pos.Y, pos.Z + 0.5);
            entity.Pos.SetFrom(entity.Pos);

            // Tagged before spawning: Initialize runs inside SpawnEntity and the AI tasks resolve
            // their match from these attributes on their first tick.
            BotMatchLink.Tag(entity, uid, runningEvent.Id, meleeDamage);

            sapi.World.SpawnEntity(entity);

            // The engine removes a corpse the same tick it dies (AllowDespawn defaults to true),
            // which would defeat the delayed removal. Removal is explicit instead: ArenaBotsEB
            // lingers the corpse, DespawnAll sweeps the rest at match end.
            if (entity is EntityAgent agent) agent.AllowDespawn = false;

            // EntityArenaBot already reports AlwaysActive, but a config-overridden entity type must
            // not fall asleep either: an inactive entity runs no AI (bot-only matches stood still).
            entity.AlwaysActive = true;
            entity.State = EnumEntityState.Active;

            entity.GetBehavior<EntityBehaviorNameTag>()?.SetName(displayName);

            // Movement hold: without it the AI wanders during the pedestal freeze and rubber-bands
            // against the freeze anchor. Refusing to START a task is not enough (a running task is
            // never re-asked), hence the StopTasks call in FreezeAi.
            var taskAi = entity.GetBehavior<EntityBehaviorTaskAI>();
            if (taskAi != null)
                taskAi.TaskManager.OnShouldExecuteTask += _ => !runningEvent.MovementFrozen;

            var health = entity.GetBehavior<EntityBehaviorHealth>();
            if (health != null && maxHealth > 0f)
            {
                health.BaseMaxHealth = maxHealth;
                health.Health = health.MaxHealth;
                health.MarkDirty();

                // Diagnostic: separates "hit never reached the server" from "hit was cancelled".
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

            // Id and position catch the invisible failure: a body the client renders but the server
            // no longer has ("no such entity found in his range").
            var hp = entity.GetBehavior<EntityBehaviorHealth>();
            log.Notification("Spawned {0} ({1}) id={2} at {3} alive={4} hp={5}",
                displayName, uid, entity.EntityId, entity.Pos.AsBlockPos, entity.Alive,
                hp == null ? "NO HEALTH BEHAVIOR" : $"{hp.Health:0.#}/{hp.MaxHealth:0.#}");

            return entity;
        }

        /// <summary>Display name of a bot uid, or null if this match never had such a bot.</summary>
        public string NameOf(string uid)
            => uid != null && names.TryGetValue(uid, out var name) ? name : null;

        /// <summary>Puts an item in the bot's right hand. Anything unresolvable is logged and skipped.</summary>
        public void GiveWeapon(string uid, string itemCode)
        {
            if (string.IsNullOrEmpty(itemCode)) return;
            // Hands live on EntityAgent, not on the Entity base type.
            var slot = (EntityOf(uid) as EntityAgent)?.RightHandItemSlot;
            if (slot == null) return;

            var loc = new AssetLocation(itemCode);
            var world = runningEvent.sapi.World;
            ItemStack stack = null;

            var item = world.GetItem(loc);
            if (item != null) stack = new ItemStack(item);
            else
            {
                var block = world.GetBlock(loc);
                if (block != null) stack = new ItemStack(block);
            }

            if (stack == null)
            {
                log.Warning("Weapon '{0}' not found, bot stays bare-handed.", itemCode);
                return;
            }

            slot.Itemstack = stack;
            slot.MarkDirty();
        }

        /// <summary>Teleports a bot's body, for the same reasons a player gets teleported.</summary>
        public void Teleport(string uid, double x, double y, double z)
            => EntityOf(uid)?.TeleportToDouble(x, y, z);

        /// <summary>
        /// Removes every body at match end; must run even on an abnormal end or the arena keeps a
        /// dozen idle bots standing on it.
        /// </summary>
        public void DespawnAll()
        {
            foreach (var entity in bodies.Values)
            {
                if (entity == null || entity.World == null) continue;
                // Explicit despawn, not Die(): auto-despawn is disabled on these bodies (see Spawn),
                // so Die() would leave the corpse forever. DespawnEntity also notifies clients.
                runningEvent.sapi.World.DespawnEntity(entity, new EntityDespawnData { Reason = EnumDespawnReason.Removed });
            }
            bodies.Clear();
        }

        /// <summary>Forgets a single body (it died and the corpse is on its way out).</summary>
        public void Forget(string uid) => bodies.Remove(uid);
    }
}
