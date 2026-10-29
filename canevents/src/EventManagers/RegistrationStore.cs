using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers.events;

namespace canevents.src.EventManagers
{
    // Queue registrations under one lock; callers only ever get snapshots.
    internal sealed class RegistrationStore
    {
        private readonly object _lock = new object();
        private readonly Dictionary<CANEvent, HashSet<Registration>> _byEvent  = new();
        private readonly Dictionary<string,   HashSet<Registration>> _byPlayer = new();

        // Write operations

        // False if already present.
        public bool Add(Registration reg)
        {
            lock (_lock)
            {
                if (!_byEvent.TryGetValue(reg.Event, out var evSet))
                    _byEvent[reg.Event] = evSet = new HashSet<Registration>();
                if (!evSet.Add(reg)) return false;

                foreach (var uid in reg.PlayersGuids)
                {
                    if (!_byPlayer.TryGetValue(uid, out var plSet))
                        _byPlayer[uid] = plSet = new HashSet<Registration>();
                    plSet.Add(reg);
                }
                return true;
            }
        }

        // Idempotent.
        public bool Remove(Registration reg)
        {
            lock (_lock)
            {
                if (_byEvent.TryGetValue(reg.Event, out var evSet))
                    evSet.Remove(reg);
                foreach (var uid in reg.PlayersGuids)
                    if (_byPlayer.TryGetValue(uid, out var plSet))
                        plSet.Remove(reg);
                return true;
            }
        }

        // Read operations

        // Snapshot keyed by event, safe to iterate without the lock.
        public Dictionary<CANEvent, List<Registration>> Snapshot()
        {
            lock (_lock)
            {
                var result = new Dictionary<CANEvent, List<Registration>>(_byEvent.Count);
                foreach (var kvp in _byEvent)
                    result[kvp.Key] = new List<Registration>(kvp.Value);
                return result;
            }
        }

        // Counts without copying the whole store.
        public int CountFor(CANEvent ev)
        {
            lock (_lock)
                return ev != null && _byEvent.TryGetValue(ev, out var set) ? set.Count : 0;
        }

        public List<Registration> SnapshotForPlayer(string uid)
        {
            lock (_lock)
                return _byPlayer.TryGetValue(uid, out var set) ? new List<Registration>(set) : new List<Registration>();
        }

        public bool HasPlayerRegistration(string uid, CANEvent ev)
        {
            lock (_lock)
                return _byPlayer.TryGetValue(uid, out var set) && set.Any(r => r.Event == ev);
        }

        public Registration FindRegistration(string uid, CANEvent ev)
        {
            lock (_lock)
            {
                if (!_byPlayer.TryGetValue(uid, out var set)) return null;
                foreach (var reg in set)
                    if (reg.Event == ev) return reg;
                return null;
            }
        }

        // Bulk-drop operations

        // Returns what was removed with the affected uids, for notifying and QueueManager cleanup.
        public List<(Registration reg, List<string> affected)> DropWhere(Predicate<Registration> predicate)
        {
            var result = new List<(Registration, List<string>)>();
            lock (_lock)
            {
                var toRemove = new List<Registration>();
                foreach (var set in _byEvent.Values)
                    foreach (var reg in set)
                        if (predicate(reg)) toRemove.Add(reg);

                foreach (var reg in toRemove)
                {
                    if (_byEvent.TryGetValue(reg.Event, out var evSet)) evSet.Remove(reg);
                    foreach (var uid in reg.PlayersGuids)
                        if (_byPlayer.TryGetValue(uid, out var plSet)) plSet.Remove(reg);
                    result.Add((reg, new List<string>(reg.PlayersGuids)));
                }
            }
            return result;
        }
    }
}
