using canevents.src.EventManagers.events;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace canevents.src.EventManagers.eventBehavior.Shared
{
    // Shared by teleport-in, respawns and bots so they all agree on a team's spawn.
    public static class EventSpawns
    {
        // perTeam layout: offset by member index so teammates don't stack.
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

        // On false, fall back to world spawn, not (0,0,0).
        public static bool TryGetRandom(RunningEvent runningEvent, out PlayerSpawnPos pos)
        {
            pos = null;
            var spawns = runningEvent?.Place?.spawnPositions;
            if (spawns == null || spawns.Count == 0) return false;

            var bp = spawns[runningEvent.sapi.World.Rand.Next(spawns.Count)];
            pos = new PlayerSpawnPos { x = bp.X, y = bp.Y, z = bp.Z };
            return true;
        }

        // PlayerSpawnPos has no dimension; the respawn patch uses ArenaDimension.
        public static bool TryGetTeamSpawn(RunningEvent runningEvent, string playerUid, out PlayerSpawnPos pos)
        {
            var bp = Resolve(runningEvent, playerUid);
            pos = bp == null ? null : new PlayerSpawnPos { x = bp.X, y = bp.Y, z = bp.Z };
            return bp != null;
        }

        public static int ArenaDimension(RunningEvent runningEvent)
        {
            var place = runningEvent?.Place;
            var spawns = place?.spawnPositions;
            if (spawns != null && spawns.Count > 0 && spawns[0] != null) return spawns[0].dimension;
            return place?.BoundsMin?.dimension ?? 0;
        }

        // Layout by spawn count: perPlayer (>= players), perTeam (>= teams), else cycle.
        public static BlockPos Resolve(RunningEvent runningEvent, string playerUid)
        {
            var spawns = runningEvent?.Place?.spawnPositions;
            if (spawns == null || spawns.Count == 0) return null;

            var groups = runningEvent.ParticipantsByGroupsBefore;
            int teamIdx = -1, memberIdx = -1, teamPrefix = 0, totalPlayers = 0;
            if (groups != null)
            {
                for (int t = 0; t < groups.Count; t++)
                {
                    if (teamIdx < 0)
                    {
                        int m = 0;
                        foreach (var uid in groups[t])
                        {
                            if (uid == playerUid) { teamIdx = t; memberIdx = m; break; }
                            m++;
                        }
                        if (teamIdx < 0) teamPrefix += groups[t].Count;
                    }
                    totalPlayers += groups[t].Count;
                }
            }

            int count = spawns.Count;
            int idx;
            var off = new Vec3i(0, 0, 0);
            if (teamIdx < 0 || (count < totalPlayers && count < groups.Count))
            {
                // cycle, also for a uid in no team
                idx = runningEvent.IndexInStartingLineup(playerUid);
                if (idx < 0) idx = 0;
                idx %= count;
            }
            else if (count >= totalPlayers && !runningEvent.PerTeamSpawns)
            {
                idx = (teamPrefix + memberIdx) % count;                 // perPlayer
            }
            else
            {
                // perTeam; a misconfigured place with fewer spawns than teams wraps instead of throwing.
                idx = teamIdx % count;
                if (memberIdx < memberOffsets.Length) off = memberOffsets[memberIdx];
            }

            // idx is a line-up slot; MapSpawnIndex applies the shuffle (RunningEvent.EnableSpawnShuffle).
            var bp = spawns[runningEvent.MapSpawnIndex(idx)];
            return new BlockPos(bp.X + off.X, bp.Y + off.Y, bp.Z + off.Z, bp.dimension);
        }

        public static string LayoutName(int spawnCount, int totalPlayers, int teamCount)
        {
            if (spawnCount >= totalPlayers) return "perPlayer";
            if (spawnCount >= teamCount) return "perTeam";
            return "cycle";
        }
    }
}
