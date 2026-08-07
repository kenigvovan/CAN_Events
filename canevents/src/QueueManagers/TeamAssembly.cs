using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace canevents.src.QueueManagers
{
    public class TeamAssembly
    {
        public List<TeamShard> Shards { get; set; } = new List<TeamShard>();

        /// <summary>
        /// Everyone across all shards, deduplicated. Populated only by <see cref="AddShard"/> (no
        /// setter) so the roster cannot disagree with the shards; removal is still allowed.
        /// </summary>
        public List<string> PlayersGuids { get; } = new List<string>();
        public bool Completed { get; set; } = false;
        public void AddShard(TeamShard shard)
        {
            Shards.Add(shard);
            foreach (var reg in shard.Registrations)
            {
                foreach (var pl in reg.PlayersGuids)
                {
                    if (!PlayersGuids.Contains(pl))
                    {
                        PlayersGuids.Add(pl);
                    }
                }
            }
        }
        /// <summary>
        /// Adds one team built from several separate registrations - what the solo-collecting
        /// managers do once they have decided who plays with whom.
        /// </summary>
        public void AddTeam(IReadOnlyList<EventManagers.Registration> members)
        {
            if (members == null || members.Count == 0) return;
            var shard = new TeamShard(members[0]);
            for (int i = 1; i < members.Count; i++) shard.Registrations.Add(members[i]);
            AddShard(shard);
        }

        public List<HashSet<string>> GetPlayerGuidsGroups()
        {
            return Shards.Select(it => it.GetPlayerGuids()).ToList();
        }
        public List<string> GetPlayerGuids()
        {
            return Shards.SelectMany(it => it.GetPlayerGuids()).Distinct().ToList();
        }
    }
}
