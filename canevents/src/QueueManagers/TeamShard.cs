using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using canevents.src.EventManagers;

namespace canevents.src.QueueManagers
{
    public class TeamShard
    {
        public List<Registration> Registrations { get; set; }
        public int PlayersCount => Registrations.Sum(r => r.PlayersCount);

        public TeamShard(Registration registration)
        {
            Registrations = new List<Registration> { registration };
        }
        public HashSet<string> GetPlayerGuids()
        {
            return Registrations.SelectMany(it => it.PlayersGuids).ToHashSet();
        }
    }
}
