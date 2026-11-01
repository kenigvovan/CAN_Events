using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using canevents.src.Mobs;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace canevents.src.Compat
{
    // canrpgmobs as an optional mob provider, by name only: nothing here may break when the mod is
    // absent or changes. Wave lines with "rpg" / "rpgGroup" are simply left out without it.
    //   - spawn through its MobSpawner, no loot from match mobs
    //   - EBRpgMob.IsEnemy narrowed to what the match director allows (their threat and brain code
    //     calls it directly, past the vanilla CanSense filter)
    //   - summons of a match mob join the same match, so the wave waits for them and they are cleaned up
    public sealed class RpgMobsProvider : IMobProvider
    {
        public const string ModId = "canrpgmobs";
        private const string IdentityTree = "canrpgmobs";

        private static MethodInfo trySpawn;
        private static MethodInfo trySpawnGroup;
        private static Type requestType;
        private static string missing;

        public string Id => ModId;

        public bool Handles(MobEntry entry) => entry.Rpg != null || entry.RpgGroup != null;

        public string Unavailable(ICoreServerAPI sapi, MobEntry entry)
        {
            if (!sapi.ModLoader.IsModEnabled(ModId)) return "canrpgmobs is not installed";
            Resolve();
            return missing;
        }

        private static void Resolve()
        {
            if (requestType != null || missing != null) return;

            var spawner = AccessTools.TypeByName("canrpgmobs.Runtime.Spawning.MobSpawner");
            requestType = AccessTools.TypeByName("canrpgmobs.Runtime.Spawning.MobSpawnRequest");
            trySpawn = spawner == null ? null : AccessTools.Method(spawner, "TrySpawn");
            trySpawnGroup = spawner == null ? null : AccessTools.Method(spawner, "TrySpawnGroup");
            if (requestType == null || trySpawn == null || trySpawnGroup == null)
            {
                missing = "this canrpgmobs version has no MobSpawner.TrySpawn/TrySpawnGroup we know";
                requestType = null;
            }
        }

        public List<Entity> Spawn(ICoreServerAPI sapi, MobEntry entry, BlockPos pos, Action<Entity> beforeSpawn)
        {
            var spawned = new List<Entity>();
            if (Unavailable(sapi, entry) != null) return spawned;

            object level = entry.Level > 0 ? (object)entry.Level : null;
            string error;
            if (entry.RpgGroup != null)
            {
                var args = new object[] { sapi, entry.RpgGroup, pos, level, entry.Faction, 32, null, null, false, null };
                if (trySpawnGroup.Invoke(null, args) is IEnumerable group)
                    foreach (var body in group)
                        if (body is Entity e) spawned.Add(e);
                error = args[6] as string;
            }
            else
            {
                var at = new Vec3d(pos.X + 0.5, pos.InternalY, pos.Z + 0.5);
                float yaw = (float)(sapi.World.Rand.NextDouble() * GameMath.TWOPI);
                var request = Activator.CreateInstance(requestType,
                    entry.Rpg, at, level, yaw, null, null, entry.Faction, entry.Tier, false, 0L, null);
                var args = new object[] { sapi, request, null };
                if (trySpawn.Invoke(null, args) is Entity e) spawned.Add(e);
                error = args[2] as string;
            }

            if (spawned.Count == 0 && error != null)
                sapi.World.Logger.Warning("[mobs] canrpgmobs refused {0}: {1}", entry.Describe(), error);

            foreach (var body in spawned)
            {
                beforeSpawn?.Invoke(body);
                NoLoot(body);
            }
            return spawned;
        }

        public static void NoLoot(Entity body)
        {
            var identity = body?.WatchedAttributes?.GetTreeAttribute(IdentityTree);
            if (identity == null) return;
            identity.SetBool("noloot", true);
            body.WatchedAttributes.MarkPathDirty(IdentityTree);
        }

        public static void Apply(Harmony harmony, ICoreServerAPI sapi)
        {
            MobProviders.Register(new RpgMobsProvider());
            if (!sapi.ModLoader.IsModEnabled(ModId)) return;

            var log = Log.For(sapi.World.Logger, "mobs");
            try
            {
                var brain = AccessTools.TypeByName("canrpgmobs.Runtime.Behaviors.EBRpgMob");
                var isEnemy = brain == null ? null : AccessTools.Method(brain, "IsEnemy", new[] { typeof(Entity) });
                var adopt = brain == null ? null : AccessTools.Method(brain, "AdoptSummon");
                if (isEnemy == null)
                {
                    log.Warning("canrpgmobs is installed but EBRpgMob.IsEnemy was not found; its mobs may attack spectators in matches.");
                    return;
                }
                harmony.Patch(isEnemy, postfix: new HarmonyMethod(typeof(RpgMobsProvider), nameof(IsEnemyGate)));
                if (adopt != null)
                    harmony.Patch(adopt, postfix: new HarmonyMethod(typeof(RpgMobsProvider), nameof(SummonJoins)));
                log.Notification("canrpgmobs mobs can be used in match waves.");
            }
            catch (Exception e)
            {
                log.Error("Could not hook canrpgmobs targeting: {0}", e);
            }
        }

        public static void IsEnemyGate(EntityBehavior __instance, Entity other, ref bool __result)
        {
            var self = __instance?.entity;
            if (__result && MatchMobLink.IsMatchMob(self)) __result = MatchMobLink.MayTarget(self, other);
        }

        public static void SummonJoins(EntityBehavior __instance, Entity summoned)
        {
            var owner = __instance?.entity;
            if (summoned == null || !MatchMobLink.IsMatchMob(owner)) return;
            NoLoot(summoned);
            MatchMobManager.Of(MatchMobLink.MatchIdOf(owner))?.Adopt(summoned, summoned.Pos.AsBlockPos, MatchMobLink.DamageMulOf(owner));
        }
    }
}
