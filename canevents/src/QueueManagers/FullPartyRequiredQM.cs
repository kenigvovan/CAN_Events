using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers;
using canevents.src.EventManagers.events;

namespace canevents.src.QueueManagers
{
    // Whole parties only. Starts at MaxPartiesCount, or at MinPartiesCount after QueueFillSeconds.
    public class FullPartyRequiredQueueManager : QueueManager
    {
        private readonly object _lock = new object();

        private readonly Dictionary<CANEvent, List<TeamAssembly>> _partial = new();
        private readonly Dictionary<TeamAssembly, DateTime> _countdownStart = new();

        public override IReadOnlyList<TeamAssembly> TryAssemble(CANEvent canEvent, IReadOnlyList<Registration> pending)
        {
            lock (_lock)
            {
                if (!_partial.TryGetValue(canEvent, out var partialList))
                    _partial[canEvent] = partialList = new List<TeamAssembly>();

                var eb = canEvent.AssemblingEB;

                foreach (var reg in pending)
                {
                    if (reg.MarkedUsed) continue;
                    if (AnyPlayerBusy(reg)) continue;

                    var target = partialList.FirstOrDefault(
                        ta => !ta.Completed && ta.Shards.Count < eb.MaxPartiesCount);
                    if (target == null) { target = new TeamAssembly(); partialList.Add(target); }

                    target.AddShard(new TeamShard(reg));
                    reg.MarkedUsed = true;
                }

                var completed = new List<TeamAssembly>();
                foreach (var ta in partialList.ToList())
                {
                    if (ta.Completed) continue;

                    // Recheck: someone may have joined another match meanwhile (would clobber their backup).
                    if (ta.Shards.Any(s => s.Registrations.Any(r => AnyPlayerBusy(r))))
                        continue;

                    int count     = ta.Shards.Count;
                    bool allValid = ta.Shards.All(s => s.PlayersCount >= eb.MinPartySize);

                    if (count >= eb.MaxPartiesCount && allValid)
                    {
                        Complete(ta, partialList, completed);
                        _countdownStart.Remove(ta);
                    }
                    else if (count >= eb.MinPartiesCount && allValid)
                    {
                        // Checked in the same pass that starts the wait, so queueFillSeconds 0 means no wait at all
                        // rather than "until the next queue tick".
                        if (!_countdownStart.TryGetValue(ta, out var startTime))
                            _countdownStart[ta] = startTime = DateTime.UtcNow;
                        if ((DateTime.UtcNow - startTime).TotalSeconds >= eb.QueueFillSeconds)
                        {
                            Complete(ta, partialList, completed);
                            _countdownStart.Remove(ta);
                        }
                    }
                    else
                    {
                        _countdownStart.Remove(ta);
                    }
                }

                return completed;
            }
        }

        private static void Complete(TeamAssembly ta, List<TeamAssembly> partialList, List<TeamAssembly> completed)
        {
            ta.Completed = true;
            partialList.Remove(ta);
            completed.Add(ta);
        }

        public override (bool ok, string message) CheckRegistrationIsSuitable(Registration reg)
            => PartySizeValidator.Check(reg);

        public override void OnRegistrationRemoved(Registration reg)
        {
            lock (_lock)
            {
                if (!_partial.TryGetValue(reg.Event, out var partialList)) return;

                int minParties = reg.Event.AssemblingEB.MinPartiesCount;
                foreach (var ta in partialList)
                {
                    foreach (var shard in ta.Shards)
                        shard.Registrations.Remove(reg);
                    ta.Shards.RemoveAll(s => s.Registrations.Count == 0);
                    foreach (var uid in reg.PlayersGuids)
                        ta.PlayersGuids.Remove(uid);

                    if (ta.Shards.Count < minParties)
                        _countdownStart.Remove(ta);
                }

                // Otherwise these countdown entries leak.
                foreach (var ta in partialList)
                    if (ta.Shards.Count == 0) _countdownStart.Remove(ta);

                partialList.RemoveAll(ta => ta.Shards.Count == 0);
            }
        }
    }
}
