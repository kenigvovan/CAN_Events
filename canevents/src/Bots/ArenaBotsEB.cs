using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers;
using canevents.src.EventManagers.eventBehavior.Shared;
using canevents.src.EventManagers.events;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace canevents.src.EventManagers.eventBehavior
{
    // Spawns bodies for bot uids already on the roster (BotFiller) and despawns them at the end.
    // Attributes:
    //   health          (float, 15)
    //   meleeDamage     (float, 2)    bare-handed damage, a weapon overrides it
    //   weapons         (string[])    one random per bot, empty = bare-handed
    //   announceDeaths  (bool, true)
    public class ArenaBotsEB : CANEventBehavior
    {
        // Enough for the death animation.
        private const int CorpseLingerMs = 4000;

        // Roughly a player's respawn screen.
        private const int OpenMatchRespawnMs = 3000;

        private float botHealth = 15f;
        private float botMeleeDamage = 2f;
        private readonly List<string> weapons = new List<string>();
        private bool announceDeaths = true;
        // Same format as for players, see EventOutfit.
        private List<string[]> outfits = new List<string[]>();

        // Mode's wardrobe if it has one, null otherwise.
        private EventManagers.eventBehavior.Shared.IOutfitWardrobe wardrobe;

        private Bots.ArenaBotManager botManager;

        public ArenaBotsEB(string name, JsonObject attributes, RunningEvent runningEvent)
            : base(name, attributes, runningEvent) { }

        public override string Name => BehaviorNames.ArenaBots;

        public override void Initialize()
        {
            botHealth = Config.Float("health", 15f);
            botMeleeDamage = Config.Float("meleeDamage", 2f);
            announceDeaths = Config.Bool(BehaviorConfigKeys.AnnounceDeaths, true);
            outfits = EventManagers.eventBehavior.Shared.EventOutfit.Parse(Attributes);

            weapons.Clear();
            var configured = Config.Raw("weapons")?.AsArray();
            if (configured != null)
            {
                foreach (var entry in configured)
                {
                    string code = entry.AsString();
                    if (!string.IsNullOrEmpty(code)) weapons.Add(code);
                }
            }

            // Read by EventOutfit, marked here so WarnUnknown doesn't complain.
            Config.Has(BehaviorConfigKeys.Outfits);
            Config.WarnUnknown();
        }

        public override void OnBeforeStart()
        {
            botManager = runningEvent.Bots;

            // Use the mode's wardrobe if it has one, so bots don't duplicate a player's outfit
            // (matters in hidden-role modes).
            wardrobe = runningEvent.GetBehavior<EventManagers.eventBehavior.Shared.IOutfitWardrobe>();
            if (wardrobe?.Wardrobe == null || wardrobe.Wardrobe.Count == 0)
            {
                wardrobe = null;
                outfits = EventManagers.eventBehavior.Shared.EventOutfit.GetEffectiveOutfits(
                    runningEvent.Event?.Code, runningEvent.OutfitManager, outfits);
            }
        }

        public override void OnStart()
        {
            foreach (var uid in runningEvent.ParticipantsBefore)
            {
                if (!Bots.BotIdentity.IsBot(uid)) continue;
                if (!SpawnBody(uid)) runningEvent.EliminatePlayer(uid);
            }
        }

        // A body at the bot's spawn, armed and dressed. False when it can't be placed at all.
        public bool SpawnBody(string uid)
        {
            var world = runningEvent.sapi.World;
            var pos = ResolveSpawn(uid);
            if (pos == null)
            {
                world.Logger.Warning("[bots] No spawn point for bot {0}; it stays out of the match.", uid);
                return false;
            }

            string name = Bots.BotIdentity.NameOfUid(uid);
            // Gear in the callback: the body may only exist after the chunk loads.
            return botManager.Spawn(uid, name, pos, botHealth, botMeleeDamage, body =>
            {
                if (weapons.Count > 0)
                    botManager.GiveWeapon(uid, weapons[world.Rand.Next(weapons.Count)]);
                EventManagers.eventBehavior.Shared.EventOutfit.Dress(
                    body, runningEvent.sapi,
                    wardrobe?.Wardrobe ?? outfits,
                    uid,
                    wardrobe?.OutfitIndexOf(uid) ?? -1);
            });
        }

        // Round modes, between rounds: a survivor walks back to its spawn healed, a fallen bot gets a new body.
        public void ResetForRound(string uid)
        {
            var body = (botManager ?? runningEvent.Bots).EntityOf(uid);
            if (body == null)
            {
                SpawnBody(uid);
                return;
            }

            var pos = ResolveSpawn(uid);
            if (pos != null) body.TeleportToDouble(pos.X + 0.5, pos.Y, pos.Z + 0.5);
            var health = body.GetBehavior<EntityBehaviorHealth>();
            if (health != null)
            {
                health.Health = health.MaxHealth;
                health.MarkDirty();
            }
        }

        // Periodic position log, for debugging client/server bot desync.
        private float posLogAccum;
        private float chunkCheckAccum;

        public override bool WantsTick => true;

        public override void OnTick(float dt)
        {
            if (botManager == null || !botManager.Any) return;

            chunkCheckAccum += dt;
            if (chunkCheckAccum >= 2f)
            {
                chunkCheckAccum = 0f;
                foreach (var uid in botManager.LiveUids) VerifyChunkRegistration(botManager.EntityOf(uid));
            }

            // Position dump is for chasing stuck bots; off unless diagnostics are on.
            if (runningEvent.sapi.ModLoader.GetModSystem<canevents>()?.config?.diagnosticsLogging != true) return;
            posLogAccum += dt;
            if (posLogAccum < 10f) return;
            posLogAccum = 0f;

            foreach (var uid in botManager.LiveUids)
            {
                var body = botManager.EntityOf(uid);
                if (body == null) continue;

                var taskAi = body.GetBehavior<EntityBehaviorTaskAI>();
                runningEvent.sapi.World.Logger.Debug(
                    "[bots] pos {0} id={1} at {2} alive={3} state={4} alwaysActive={5} task={6}",
                    body.GetName(), body.EntityId, body.Pos.AsBlockPos, body.Alive,
                    body.State, body.AlwaysActive,
                    taskAi?.TaskManager?.ActiveTasksBySlot?.FirstOrDefault(t => t != null)?.GetType().Name ?? "none");
            }
        }

        // A bot missing from chunk.Entities can't be hit. The engine only re-files an entity when its
        // chunk index changes and the chunk is loaded, so we fake a change via InChunkIndex3d.
        private void VerifyChunkRegistration(Entity body)
        {
            if (body == null || !body.Alive) return;

            var sapi = runningEvent.sapi;
            const int size = Vintagestory.API.Config.GlobalConstants.ChunkSize;
            int cx = (int)body.Pos.X / size;
            int cy = (int)body.Pos.InternalY / size;
            int cz = (int)body.Pos.Z / size;

            var chunk = sapi.WorldManager.GetChunk(cx, cy, cz);
            if (chunk?.Entities == null) return;   // not loaded

            for (int i = 0; i < chunk.Entities.Length && i < chunk.EntitiesCount; i++)
            {
                if (chunk.Entities[i]?.EntityId == body.EntityId) return;
            }

            // Use a real neighbor index so the "remove from old chunk" step doesn't break.
            long decoyIndex = sapi.WorldManager.ChunkIndex3D(cx + 1, cy, cz);
            long realIndex = sapi.WorldManager.ChunkIndex3D(cx, cy, cz);
            if (decoyIndex == realIndex) decoyIndex = sapi.WorldManager.ChunkIndex3D(cx, cy, cz + 1);
            body.InChunkIndex3d = decoyIndex;

            sapi.World.Logger.Warning(
                "[bots] {0} (id={1}) was missing from chunk {2}/{3}/{4} - could not be hit. Re-registering.",
                body.GetName(), body.EntityId, cx, cy, cz);
        }

        public override void OnEnd()
        {
            botManager?.DespawnAll();
        }

        // Open match: one bot taken out (its roster seat is the caller's business).
        public void Despawn(string uid)
        {
            var manager = botManager ?? runningEvent.Bots;
            var body = manager.EntityOf(uid);
            manager.Forget(uid);
            if (body?.World != null)
                runningEvent.sapi.World.DespawnEntity(body, new EntityDespawnData { Reason = EnumDespawnReason.Removed });
        }

        // Same resolver as players use.
        private BlockPos ResolveSpawn(string uid) => EventSpawns.Resolve(runningEvent, uid);

        // Called from CANEventManager.OnEntityDeath after all behaviors saw the death. Bots don't respawn
        // mid-match; round modes bring them back between rounds (ResetForRound).
        public void AfterBotDeath(string uid, Entity entity, DamageSource source)
        {
            (botManager ?? runningEvent.Bots).Forget(uid);

            // Delayed: removing on the death tick leaves a ghost body on the client.
            // Spawn disables auto-despawn, so despawn explicitly.
            var deathSource = source;
            runningEvent.sapi.Event.RegisterCallback(_ =>
            {
                if (entity.World != null)
                    runningEvent.sapi.World.DespawnEntity(entity,
                        new EntityDespawnData { Reason = EnumDespawnReason.Death, DamageSourceForDeath = deathSource });
            }, CorpseLingerMs);

            if (runningEvent.EventState != EventState.Running || !runningEvent.IsActive(uid)) return;

            // Open match: a bot comes back at its team's spawn like a player would.
            if (runningEvent.IsOpenMatch)
            {
                runningEvent.sapi.Event.RegisterCallback(_ =>
                {
                    if (runningEvent.EventState == EventState.Running && runningEvent.IsActive(uid)) SpawnBody(uid);
                }, OpenMatchRespawnMs);
                return;
            }

            if (announceDeaths && !runningEvent.Behaviors.Any(b => b.AnnouncesDeaths))
            {
                string killerName = ResolveKillerName(uid, source);
                string botName = runningEvent.NameOf(uid);
                Announce("canevents:bot-eliminated", botName, killerName);
                EventBroadcaster.KillFeed(runningEvent, killerName, botName, "eliminated");
            }

            runningEvent.EliminatePlayer(uid);
        }

        private string ResolveKillerName(string victimUid, DamageSource source)
        {
            // Hidden-role modes (murder mystery) must not reveal the killer.
            if (runningEvent.Scorer?.HidesKillerIdentity == true)
                return Lang.Get("canevents:killer-someone");

            return KillAttribution.Resolve(runningEvent, victimUid, source).KillerName;
        }
    }
}
