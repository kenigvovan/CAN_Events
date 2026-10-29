using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers.events;
using Vintagestory.API.Config;

namespace canevents.src.EventManagers
{
    // /eventadmin reload.
    public partial class CANEventManager
    {
        // Live matches keep their own CANEvent. Registrations are keyed by the CANEvent object, so ones
        // for changed/removed events would be stranded: refuse and return their codes, or drop them if asked.
        public List<string> ReplaceEventDefinitions(IReadOnlyCollection<CANEvent> next, bool dropStrandedQueues,
            out List<string> droppedQueues)
        {
            droppedQueues = new List<string>();
            var keep = new HashSet<CANEvent>(next);

            var strandedCodes = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var codesByUid = new Dictionary<string, SortedSet<string>>();
            foreach (var (ev, regs) in _store.Snapshot())
            {
                if (regs.Count == 0 || keep.Contains(ev)) continue;
                strandedCodes.Add(ev.Code);
                foreach (var reg in regs)
                    foreach (var uid in reg.PlayersGuids)
                    {
                        if (!codesByUid.TryGetValue(uid, out var codes))
                            codesByUid[uid] = codes = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                        codes.Add(ev.Code);
                    }
            }

            if (strandedCodes.Count > 0)
            {
                if (!dropStrandedQueues) return strandedCodes.ToList();

                // Before remapping QMs: the drop looks the QM up by code.
                DropRegistrations(reg => !keep.Contains(reg.Event),
                    uid => codesByUid.TryGetValue(uid, out var codes)
                        ? Lang.Get("canevents:reload-queue-dropped", string.Join(", ", codes))
                        : null);
                droppedQueues = strandedCodes.ToList();
            }

            // In place, others hold a reference to this set.
            Events.Clear();
            Events.UnionWith(next);

            // Removed codes stay mapped; a live match may still need its QM.
            foreach (var ev in next) AssignQueueManager(ev);

            // Load item sets for new events.
            foreach (var store in ItemSetStores.Values)
            {
                var known = store.GetAll();
                store.Load(next.Select(e => e.Code).Where(c => !known.ContainsKey(c)).ToList());
            }

            NetworkHandler?.BroadcastEventList();
            NetworkHandler?.BroadcastAdminEventList();
            return new List<string>();
        }
    }
}
