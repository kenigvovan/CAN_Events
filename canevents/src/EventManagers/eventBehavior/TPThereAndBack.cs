using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers;
using canevents.src.EventManagers.events;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace canevents.src.EventManagers.eventBehavior
{
    public class TPThereAndBack : CANEventBehavior
    {
        private Dictionary<string, BlockPos> SavedPlayerPos = new Dictionary<string, BlockPos>();

        // Pre-event position mirrored to player ModData at teleport-in, so a crash (OnEnd never runs)
        // can still return them home on next login. Read + cleared by CANEventManager on reconnect.
        public const string ReturnPosModDataKey = "canevents_return_pos";

        // Per-team mode: offsets by member index so teammates don't stack on one block.
        private static readonly Vec3i[] memberOffsets = new[]
        {
            new Vec3i(0, 0, 0),
            new Vec3i(1, 0, 0),
            new Vec3i(-1, 0, 0),
            new Vec3i(0, 0, 1),
            new Vec3i(0, 0, -1),
            new Vec3i(2, 0, 0),
            new Vec3i(-2, 0, 0),
            new Vec3i(0, 0, 2),
            new Vec3i(0, 0, -2),
        };

        public TPThereAndBack(string name, JsonObject attributes, RunningEvent runningEvent) : base(name, attributes, runningEvent)
        {
        }

        public override string Name { get => "TPThereAndBack"; set { } }

        // No Clone() override: the base Activator clone is exactly equivalent.

        public override void OnStart()
        {
            var logger = runningEvent.sapi.World.Logger;
            if (runningEvent.Place == null)
            {
                logger.Error("TPThereAndBack.OnStart: runningEvent.Place is null, no teleport");
                return;
            }
            var spawnPositions = runningEvent.Place.spawnPositions;
            int totalPlayers = runningEvent.ParticipantsBefore?.Count ?? 0;
            int teamCount = runningEvent.ParticipantsByGroupsBefore?.Count ?? 0;
            string mode = ResolveMode(spawnPositions?.Count ?? 0, totalPlayers, teamCount);
            logger.Notification("TPThereAndBack.OnStart: place='{0}' spawnCount={1} players={2} teams={3} mode={4}",
                runningEvent.Place.Name,
                spawnPositions?.Count ?? -1,
                totalPlayers, teamCount, mode);

            if (spawnPositions == null || spawnPositions.Count == 0)
            {
                logger.Error("No spawn positions defined for event place {0}", runningEvent.Place.Name);
                return;
            }

            foreach (var plGuid in runningEvent.ParticipantsBefore)
            {
                // Bots have no player entity; ArenaBotsEB spawns their bodies on the same spawn points.
                if (Bots.BotIdentity.IsBot(plGuid)) continue;
                // Dropped out before the match started: still on the roster for rewards, but never in
                // play, so no spawn point and no free trip in if they log back in during the countdown.
                if (!runningEvent.IsActive(plGuid)) continue;

                var pl = runningEvent.sapi.World.PlayerByUid(plGuid) as IServerPlayer;
                if (pl == null)
                {
                    logger.Warning("TPThereAndBack: participant uid={0} not found in World", plGuid);
                    continue;
                }
                var (sp, off) = ResolveSpawn(plGuid, spawnPositions);
                if (sp == null) continue;
                double x = sp.X + 0.5 + off.X;
                double y = sp.Y + 0.5 + off.Y;
                double z = sp.Z + 0.5 + off.Z;
                // Captured right before teleporting, not in OnBeforeStart: the player can walk around
                // during the countdown, and only players actually teleported get a saved position.
                var returnPos = pl.Entity.Pos.AsBlockPos.Copy();
                SavedPlayerPos[plGuid] = returnPos;
                WriteReturnPos(pl, returnPos);
                logger.Notification("TPThereAndBack: teleport {0} -> {1:F1},{2:F1},{3:F1}", pl.PlayerName, x, y, z);
                pl.Entity.TeleportToDouble(x, y, z);
            }
        }

        public override void OnEnd()
        {
            foreach (var plGuid in runningEvent.ParticipantsBefore)
            {
                var pl = runningEvent.sapi.World.PlayerByUid(plGuid) as IServerPlayer;
                if (pl == null) continue;
                if (SavedPlayerPos.TryGetValue(plGuid, out var savedPos))
                {
                    pl.Entity.TeleportToDouble(savedPos.X + 0.5, savedPos.Y + 0.5, savedPos.Z + 0.5);
                }
                // Drop the crash-recovery marker so a later login doesn't teleport them again.
                pl.RemoveModdata(ReturnPosModDataKey);
            }
        }

        public override void OnParticipantRespawn(IServerPlayer player)
        {
            // No-op: respawn points come from the Harmony patch on ServerPlayer.GetSpawnPosition for
            // IProvidesRespawnPoint behaviors. Without one (one-life modes) the player is eliminated.
        }

        // Spawn point + offset for a player. Mode auto-detected by spawn-point count: perPlayer (a
        // dedicated point each, teams in contiguous blocks), perTeam (one point per team, members
        // spread by offsets), or cycle when there are fewer points than teams.
        private (BlockPos pos, Vec3i offset) ResolveSpawn(string playerUid, List<BlockPos> spawnPositions)
        {
            var groups = runningEvent.ParticipantsByGroupsBefore;
            int teamIdx = -1, memberIdx = -1;
            int teamSizesPrefix = 0;
            int totalPlayers = 0;
            if (groups != null)
            {
                for (int t = 0; t < groups.Count; t++)
                {
                    int m = 0;
                    bool found = false;
                    foreach (var uid in groups[t])
                    {
                        if (uid == playerUid) { teamIdx = t; memberIdx = m; found = true; break; }
                        m++;
                    }
                    if (!found && teamIdx < 0) teamSizesPrefix += groups[t].Count;
                    totalPlayers += groups[t].Count;
                }
            }

            int spawnCount = spawnPositions.Count;

            var zero = new Vec3i(0, 0, 0);
            if (teamIdx < 0)
            {
                // Player not found in groups, fallback to cycle.
                int idx = runningEvent.IndexInStartingLineup(playerUid);
                if (idx < 0) idx = 0;
                return (spawnPositions[idx % spawnCount], zero);
            }

            // perPlayer: enough points for everyone
            if (spawnCount >= totalPlayers)
            {
                int globalIdx = teamSizesPrefix + memberIdx;
                if (globalIdx >= spawnCount) globalIdx %= spawnCount;
                return (spawnPositions[globalIdx], zero);
            }

            // perTeam: one point per team, spread members with offsets
            int teamCount = groups.Count;
            if (spawnCount >= teamCount)
            {
                var basePoint = spawnPositions[teamIdx];
                var off = memberIdx < memberOffsets.Length ? memberOffsets[memberIdx] : zero;
                return (basePoint, off);
            }

            // cycle fallback
            int absIdx = runningEvent.IndexInStartingLineup(playerUid);
            if (absIdx < 0) absIdx = 0;
            return (spawnPositions[absIdx % spawnCount], zero);
        }

        private static string ResolveMode(int spawnCount, int totalPlayers, int teamCount)
        {
            if (spawnCount >= totalPlayers) return "perPlayer";
            if (spawnCount >= teamCount) return "perTeam";
            return "cycle";
        }

        private static void WriteReturnPos(IServerPlayer player, BlockPos pos)
        {
            var tree = new TreeAttribute();
            tree.SetInt("x", pos.X);
            tree.SetInt("y", pos.Y);
            tree.SetInt("z", pos.Z);
            player.SetModdata(ReturnPosModDataKey, tree.ToBytes());
        }
    }
}
