using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers.events;

namespace canevents.src.EventManagers
{
    /// <summary>Thread-safe store for queue registrations: all collections are owned here under a
    /// single lock, and callers never touch the underlying data directly.</summary>
    internal sealed class RegistrationStore
    {
        private readonly object _lock = new object();
        private readonly Dictionary<CANEvent, HashSet<Registration>> _byEvent  = new();
        private readonly Dictionary<string,   HashSet<Registration>> _byPlayer = new();

        // Write operations

        /// <summary>Adds the registration. Returns false if it was already present.</summary>
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

        /// <summary>Removes the registration. Idempotent: safe to call if already absent.</summary>
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

        /// <summary>Snapshot of all registrations, keyed by event. Safe to iterate without locking.</summary>
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

        /// <summary>How many registrations are queued for one event. Exists so a caller that only
        /// wants the number does not copy the whole store to count one list.</summary>
        public int CountFor(CANEvent ev)
        {
            lock (_lock)
                return ev != null && _byEvent.TryGetValue(ev, out var set) ? set.Count : 0;
        }

        /// <summary>Snapshot of all registrations for a player. Returns empty list if none.</summary>
        public List<Registration> SnapshotForPlayer(string uid)
        {
            lock (_lock)
                return _byPlayer.TryGetValue(uid, out var set) ? new List<Registration>(set) : new List<Registration>();
        }

        /// <summary>True if the player has any pending registration for the given event.</summary>
        public bool HasPlayerRegistration(string uid, CANEvent ev)
        {
            lock (_lock)
                return _byPlayer.TryGetValue(uid, out var set) && set.Any(r => r.Event == ev);
        }

        /// <summary>Returns the first pending registration for (uid, event), or null.</summary>
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

        /// <summary>Removes all registrations matching the predicate, returning (registration, affected
        /// UIDs) pairs so the caller can notify players and clean up QueueManager partial state.</summary>
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
