using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers;
using canevents.src.EventManagers.events;

namespace canevents.src.QueueManagers
{
    /// <summary>
    /// Accepts only whole parties, one registration per team slot, collected across ticks.
    /// Completes at MaxPartiesCount immediately, or at MinPartiesCount after a QueueFillSeconds
    /// countdown (cancelled if the count drops back below the minimum).
    /// </summary>
    public class FullPartyRequiredQueueManager : QueueManager
    {
        private readonly object _lock = new object();

        // Incomplete assemblies waiting for more parties, keyed by event.
        private readonly Dictionary<CANEvent, List<TeamAssembly>> _partial = new();
        // Per-assembly timestamp of when the fill-countdown started.
        private readonly Dictionary<TeamAssembly, DateTime> _countdownStart = new();

        public override IReadOnlyList<TeamAssembly> TryAssemble(CANEvent canEvent, IReadOnlyList<Registration> pending)
        {
            lock (_lock)
            {
                if (!_partial.TryGetValue(canEvent, out var partialList))
                    _partial[canEvent] = partialList = new List<TeamAssembly>();

                var eb = canEvent.AssemblingEB;

                // Phase 1: assign new registrations to partial assemblies
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

                // Phase 2: check completion conditions
                var completed = new List<TeamAssembly>();
                foreach (var ta in partialList.ToList())
                {
                    if (ta.Completed) continue;

                    // Re-check busy at completion time: a player waiting here may have since entered
                    // another event's match, and starting now would double-teleport them and clobber
                    // their inventory backup. Defer until they are free again.
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
                        if (!_countdownStart.TryGetValue(ta, out var startTime))
                        {
                            _countdownStart[ta] = DateTime.UtcNow;
                        }
                        else if ((DateTime.UtcNow - startTime).TotalSeconds >= eb.QueueFillSeconds)
                        {
                            // Countdown expired, start with what we have.
                            Complete(ta, partialList, completed);
                            _countdownStart.Remove(ta);
                        }
                    }
                    else
                    {
                        // Below minimum, cancel any running countdown.
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

                    // Below minimum, the countdown is no longer valid.
                    if (ta.Shards.Count < minParties)
                        _countdownStart.Remove(ta);
                }

                // Also drop countdowns of assemblies about to be discarded: _countdownStart is keyed
                // by TeamAssembly, so entries left after removal are unreachable and leak.
                foreach (var ta in partialList)
                    if (ta.Shards.Count == 0) _countdownStart.Remove(ta);

                partialList.RemoveAll(ta => ta.Shards.Count == 0);
            }
        }
    }
}
