using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Server;

namespace canevents.src.EventManagers
{
    // Stored as int in the file and on the wire.
    public enum InventoryAuditKind
    {
        Taken = 0,

        Returned = 1,

        // Items parked under the failed key.
        RestoreFailed = 2,

        // Live inventory at that moment was not saved.
        RefusedOverwrite = 3,

        ParkedRestored = 4
    }

    public class InventoryAuditEntry
    {
        public long AtUnix;
        public InventoryAuditKind Kind;
        public string PlayerUid;
        public string PlayerName;
        public string EventCode;
        public int StackCount;
        public string Detail;
    }

    // Wrapper so fields can be added later.
    public class InventoryAuditFile
    {
        public List<InventoryAuditEntry> Entries = new List<InventoryAuditEntry>();
    }

    // Persistent copy of the [inventory] log lines, readable from the admin panel.
    public static class InventoryAuditLog
    {
        private const string FileName = "canevents/inventory-audit.json";

        // Batches the per-participant writes at match end.
        private const int FlushDelayMs = 5000;

        private static readonly object gate = new object();
        private static readonly List<InventoryAuditEntry> entries = new List<InventoryAuditEntry>();

        private static ICoreServerAPI api;
        private static bool enabled;
        private static int maxEntries = 4000;
        private static bool dirty;
        private static bool flushScheduled;

        public static bool Enabled => enabled;

        // on=false leaves the existing file alone.
        public static void Configure(ICoreServerAPI sapi, bool on, int keep)
        {
            lock (gate)
            {
                api = sapi;
                enabled = on;
                maxEntries = Math.Clamp(keep, 100, 100000);
                entries.Clear();
                dirty = false;
                flushScheduled = false;

                if (on && sapi != null)
                {
                    try
                    {
                        var loaded = sapi.LoadModConfig<InventoryAuditFile>(FileName);
                        if (loaded?.Entries != null) entries.AddRange(loaded.Entries.Where(e => e != null));
                        Trim();
                    }
                    catch (Exception e)
                    {
                        // Move it aside, the next flush would overwrite it.
                        string aside = Scoring.JsonStore.MoveAside(
                            System.IO.Path.Combine(ModPaths.Root, "inventory-audit.json"));
                        sapi.World.Logger.Warning(
                            "[inventory] could not read the audit history ({0}); starting a fresh one. The unreadable file was {1}.",
                            e.Message, aside != null ? "kept as " + aside : "NOT backed up");
                        entries.Clear();
                    }
                }
            }

            sapi?.World?.Logger?.Notification(on
                ? "[inventory] audit on - taking and returning inventories is recorded in ModConfig/"
                  + FileName + " (keeping the last " + maxEntries + " entries)."
                : "[inventory] audit off - backups still work, but nothing is recorded.");
        }

        public static void Record(ICoreServerAPI sapi, InventoryAuditKind kind, string uid, string name,
            string eventCode, int stackCount, string detail = null)
        {
            if (!enabled) return;

            lock (gate)
            {
                // Not configured yet; the server log still has it.
                if (api == null) return;

                entries.Add(new InventoryAuditEntry
                {
                    AtUnix = UnixTime.Now,
                    Kind = kind,
                    PlayerUid = uid,
                    PlayerName = name,
                    EventCode = string.IsNullOrEmpty(eventCode) ? null : eventCode,
                    StackCount = stackCount,
                    Detail = detail
                });
                Trim();
                dirty = true;
                ScheduleFlush(sapi ?? api);
            }
        }

        // Newest first.
        public static List<InventoryAuditEntry> Recent(int limit = 200, string uid = null)
        {
            lock (gate)
            {
                IEnumerable<InventoryAuditEntry> q = entries;
                if (!string.IsNullOrEmpty(uid))
                    q = q.Where(e => string.Equals(e.PlayerUid, uid, StringComparison.Ordinal));
                return q.Reverse().Take(Math.Max(1, limit)).ToList();
            }
        }

        // Last relevant entry is Taken with no Returned after it.
        public static bool HasUnsettledTake(string uid)
        {
            lock (gate)
            {
                for (int i = entries.Count - 1; i >= 0; i--)
                {
                    var e = entries[i];
                    if (!string.Equals(e.PlayerUid, uid, StringComparison.Ordinal)) continue;
                    if (e.Kind == InventoryAuditKind.Taken) return true;
                    if (e.Kind == InventoryAuditKind.Returned
                        || e.Kind == InventoryAuditKind.ParkedRestored) return false;
                }
                return false;
            }
        }

        // Called on shutdown too, the pending callback won't run.
        public static void Flush()
        {
            ICoreServerAPI sapi;
            InventoryAuditFile snapshot;

            lock (gate)
            {
                if (!enabled || !dirty || api == null) return;
                sapi = api;
                snapshot = new InventoryAuditFile { Entries = new List<InventoryAuditEntry>(entries) };
                dirty = false;
            }

            // Disk write outside the lock.
            try { sapi.StoreModConfig(snapshot, FileName); }
            catch (Exception e)
            {
                sapi.World.Logger.Warning("[inventory] could not write the audit history: {0}", e.Message);
            }
        }

        private static void ScheduleFlush(ICoreServerAPI sapi)
        {
            if (flushScheduled || sapi == null) return;
            flushScheduled = true;

            // Can throw during shutdown or off the main thread; the entry stays in memory for the next flush.
            try
            {
                sapi.Event.RegisterCallback(_ =>
                {
                    lock (gate) { flushScheduled = false; }
                    Flush();
                }, FlushDelayMs);
            }
            catch (Exception)
            {
                flushScheduled = false;
            }
        }

        // Caller holds the lock.
        private static void Trim()
        {
            int excess = entries.Count - maxEntries;
            if (excess > 0) entries.RemoveRange(0, excess);
        }
    }
}
