using canevents.src.EventManagers.events;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace canevents.src.EventManagers.eventBehavior.Shared
{
    // Spawn-point selection shared by the respawning game modes.
    public static class EventSpawns
    {
        /// <summary>
        /// Picks a random spawn point of the event's place. False when the place has none;
        /// callers must fall back to the world spawn rather than teleport to (0,0,0).
        /// </summary>
        public static bool TryGetRandom(RunningEvent runningEvent, out PlayerSpawnPos pos)
        {
            pos = null;
            var spawns = runningEvent?.Place?.spawnPositions;
            if (spawns == null || spawns.Count == 0) return false;

            var bp = spawns[runningEvent.sapi.World.Rand.Next(spawns.Count)];
            pos = new PlayerSpawnPos { x = bp.X, y = bp.Y, z = bp.Z };
            return true;
        }

        /// <summary>
        /// Spawn point of this player's TEAM, so a respawn returns them to their own base. The three
        /// layouts must match TPThereAndBack.ResolveSpawn:
        ///   perPlayer (spawns >= total players): contiguous block per team, indexed by the prefix
        ///     sum of earlier teams' sizes (indexing by team number would collide).
        ///   perTeam (spawns >= team count): one point per team, indexed by team number.
        ///   cycle (fewer points than teams): team number modulo the count.
        /// False when the place has no spawns; the caller falls back to the world spawn.
        /// </summary>
        public static bool TryGetTeamSpawn(RunningEvent runningEvent, string playerUid, out PlayerSpawnPos pos)
        {
            pos = null;
            var spawns = runningEvent?.Place?.spawnPositions;
            if (spawns == null || spawns.Count == 0) return false;

            var groups = runningEvent.ParticipantsByGroupsBefore;
            int teamIdx = -1, teamPrefix = 0, totalPlayers = 0;
            if (groups != null)
            {
                for (int t = 0; t < groups.Count; t++)
                {
                    if (teamIdx < 0)
                    {
                        if (groups[t].Contains(playerUid)) teamIdx = t;
                        else teamPrefix += groups[t].Count;
                    }
                    totalPlayers += groups[t].Count;
                }
            }

            int count = spawns.Count;
            int idx;
            if (teamIdx < 0) idx = 0;                              // unknown team: first spawn
            else if (count >= totalPlayers) idx = teamPrefix % count;   // perPlayer: team's contiguous block start
            else if (count >= groups.Count) idx = teamIdx;        // perTeam: one point per team
            else idx = teamIdx % count;                           // cycle

            // Through the match's spawn order, the same way TPThereAndBack picked the point at the
            // start: a respawn has to land on the point this team actually started from.
            var bp = spawns[runningEvent.MapSpawnIndex(idx)];
            pos = new PlayerSpawnPos { x = bp.X, y = bp.Y, z = bp.Z };
            return true;
        }
    }
}
