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
    /// <summary>
    /// Gives the match's bot uids actual bodies (spawn point, weapon, death handling) and removes
    /// them at the end. The roster already contains them via Bots.BotFiller.
    ///
    /// Attributes:
    ///   health          (float, 15)   - max health per bot
    ///   meleeDamage     (float, 2)    - bare-handed damage; a looted weapon overrides it
    ///   weapons         (string[])    - one is picked per bot; empty = bare-handed
    ///   announceDeaths  (bool, true)  - whether a bot dying is worth a broadcast line
    /// </summary>
    public class ArenaBotsEB : CANEventBehavior
    {
        // Long enough for the death animation, short enough that nobody swings at a corpse.
        private const int CorpseLingerMs = 4000;

        private float botHealth = 15f;
        private float botMeleeDamage = 2f;
        private readonly List<string> weapons = new List<string>();
        private bool announceDeaths = true;
        // Same attribute name and wardrobe the players get - see EventOutfit.
        private List<string[]> outfits = new List<string[]>();

        private Bots.ArenaBotManager botManager;
        private EntityDeathDelegate deathHandler;

        public ArenaBotsEB(string name, JsonObject attributes, RunningEvent runningEvent)
            : base(name, attributes, runningEvent) { }

        public override string Name { get => "ArenaBots"; set { } }

        public override void Initialize()
        {
            botHealth = Config.Float("health", 15f);
            botMeleeDamage = Config.Float("meleeDamage", 2f);
            announceDeaths = Config.Bool("announceDeaths", true);
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

            // EventOutfit reads "outfits" straight off Attributes; declared here so WarnUnknown
            // does not report it.
            Config.Has("outfits");
            Config.WarnUnknown();
        }

        public override void OnBeforeStart()
        {
            botManager = runningEvent.Bots;

            // No per-match death hook exists for non-players, so the global one is filtered down to
            // this match's bots. Detached in OnEnd or it keeps eliminating into a finished match.
            deathHandler = OnAnyEntityDeath;
            runningEvent.sapi.Event.OnEntityDeath += deathHandler;
        }

        public override void OnStart()
        {
            var world = runningEvent.sapi.World;

            foreach (var uid in runningEvent.ParticipantsBefore)
            {
                if (!Bots.BotIdentity.IsBot(uid)) continue;

                var pos = ResolveSpawn(uid);
                if (pos == null)
                {
                    world.Logger.Warning("[bots] No spawn point for bot {0}; it stays out of the match.", uid);
                    runningEvent.EliminatePlayer(uid);
                    continue;
                }

                // From the uid, not a counter, so scoreboard/bracket/kill feed agree on the name.
                string name = Bots.BotIdentity.NameOfUid(uid);
                // Weapon handed out from the callback: with an unloaded arena chunk there is no body
                // to put a sword in until the chunk arrives.
                string botUid = uid;
                bool spawning = botManager.Spawn(uid, name, pos, botHealth, botMeleeDamage, body =>
                {
                    if (weapons.Count > 0)
                        botManager.GiveWeapon(botUid, weapons[world.Rand.Next(weapons.Count)]);
                    EventManagers.eventBehavior.Shared.EventOutfit.Dress(
                        body, runningEvent.sapi, outfits, botUid);
                });

                // No entity type: the match still runs, it just runs without this bot.
                if (!spawning) runningEvent.EliminatePlayer(uid);
            }
        }

        // Server-side positions every ~10s: diagnostic for "I hit them but the server finds no
        // entity near me". Matching positions = client ghost bodies; diverging = movement desync.
        private float posLogAccum;
        private float chunkCheckAccum;

        public override void OnTick(float dt)
        {
            if (botManager == null || !botManager.Any) return;

            chunkCheckAccum += dt;
            if (chunkCheckAccum >= 2f)
            {
                chunkCheckAccum = 0f;
                foreach (var uid in botManager.LiveUids) VerifyChunkRegistration(botManager.EntityOf(uid));
            }

            posLogAccum += dt;
            if (posLogAccum < 10f) return;
            posLogAccum = 0f;

            foreach (var uid in botManager.LiveUids)
            {
                var body = botManager.EntityOf(uid);
                if (body == null) continue;

                // State and AlwaysActive separate the two causes of "the bots just stand there":
                // a sleeping entity (nobody in simulation range) vs a running AI with no target.
                var taskAi = body.GetBehavior<EntityBehaviorTaskAI>();
                // Debug, not Notification: fires every 10s per bot.
                runningEvent.sapi.World.Logger.Debug(
                    "[bots] pos {0} id={1} at {2} alive={3} state={4} alwaysActive={5} task={6}",
                    body.GetName(), body.EntityId, body.Pos.AsBlockPos, body.Alive,
                    body.State, body.AlwaysActive,
                    taskAi?.TaskManager?.ActiveTasksBySlot?.FirstOrDefault(t => t != null)?.GetType().Name ?? "none");
            }
        }

        /// <summary>
        /// Makes sure a bot is listed in its chunk's entity array, and re-registers it if not.
        /// Lookups walk chunk.Entities, so a body missing from that list cannot be hit at all. The
        /// engine re-files entities once a second, but only when the chunk index changed AND the
        /// target chunk is loaded, leaving bots that passed through an unloaded chunk stranded.
        /// The repair triggers that same pass by pointing InChunkIndex3d elsewhere; it only runs
        /// when the entity is genuinely absent, so it cannot double-register.
        /// </summary>
        private void VerifyChunkRegistration(Entity body)
        {
            if (body == null || !body.Alive) return;

            var sapi = runningEvent.sapi;
            const int size = Vintagestory.API.Config.GlobalConstants.ChunkSize;
            int cx = (int)body.Pos.X / size;
            int cy = (int)body.Pos.InternalY / size;
            int cz = (int)body.Pos.Z / size;

            var chunk = sapi.WorldManager.GetChunk(cx, cy, cz);
            if (chunk?.Entities == null) return;   // not loaded: nothing to register into yet

            for (int i = 0; i < chunk.Entities.Length && i < chunk.EntitiesCount; i++)
            {
                if (chunk.Entities[i]?.EntityId == body.EntityId) return;
            }

            // Any index other than the current chunk's makes the engine re-file it; a neighbour
            // rather than a bogus value, so the "remove from old chunk" step hits a real list.
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
            if (deathHandler != null)
            {
                runningEvent.sapi.Event.OnEntityDeath -= deathHandler;
                deathHandler = null;
            }
            botManager?.DespawnAll();
        }

        // Same spawn rule players get, so bots and players are laid out identically (EventSpawns).
        private BlockPos ResolveSpawn(string uid)
        {
            if (!EventSpawns.TryGetTeamSpawn(runningEvent, uid, out var spawn)) return null;
            return new BlockPos(spawn.x, spawn.y ?? 0, spawn.z, 0);
        }

        private void OnAnyEntityDeath(Entity entity, DamageSource source)
        {
            string uid = Bots.BotMatchLink.UidOf(entity);
            if (uid == null || !Bots.BotIdentity.IsBot(uid)) return;
            // Bots of other matches fire the same global event; the roster check filters them out.
            if (!runningEvent.IsActive(uid)) return;

            botManager?.Forget(uid);

            // Delayed removal: a body removed the same tick it died stays rendered client-side, and
            // swings at that ghost cannot be matched server-side. Explicit DespawnEntity because
            // Spawn disables auto-despawn.
            var deathSource = source;
            runningEvent.sapi.Event.RegisterCallback(_ =>
            {
                if (entity.World != null)
                    runningEvent.sapi.World.DespawnEntity(entity,
                        new EntityDespawnData { Reason = EnumDespawnReason.Death, DamageSourceForDeath = deathSource });
            }, CorpseLingerMs);

            string killerName = ResolveKillerName(source);
            if (announceDeaths)
            {
                string botName = entity.GetName();
                EventBroadcaster.Broadcast(runningEvent,
                    Lang.Get("canevents:bot-eliminated", botName, killerName));
                runningEvent.NetworkHandler?.SendKillFeed(runningEvent, killerName, botName, "eliminated");
            }

            // Same call the player death path ends in; flips ShouldFinish once one side is left.
            runningEvent.EliminatePlayer(uid);
        }

        private string ResolveKillerName(DamageSource source)
        {
            var cause = source?.GetCauseEntity();
            if (cause == null) return Lang.Get("canevents:killer-the-arena");

            if (cause is EntityPlayer player) return player.Player?.PlayerName ?? player.GetName();
            return cause.GetName();
        }
    }
}
