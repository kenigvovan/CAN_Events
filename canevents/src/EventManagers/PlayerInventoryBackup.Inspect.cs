using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace canevents.src.EventManagers
{
    // Read-only view of a snapshot, for admin inspection.
    public class InventoryBackupInfo
    {
        public bool Present;

        // From the failed key (restore threw).
        public bool Parked;

        // 0 for old snapshots.
        public long SavedAtUnix;

        public string EventCode;

        // Counted from the blob; old snapshots have no header count.
        public int StackCount;

        // Own-gear match: a record only, never handed back.
        public bool KeepLiveInventory;

        public float Health;
        public float MaxHealth;

        // Grouped by code; unresolved codes are kept (removed mods).
        public List<(string code, int quantity, bool resolved)> Items = new List<(string, int, bool)>();

        // -1 without a timestamp.
        public long AgeSeconds => SavedAtUnix > 0 ? Math.Max(0, UnixTime.Now - SavedAtUnix) : -1;
    }

    public static partial class PlayerInventoryBackup
    {
        // Never throws; a corrupt blob comes back Present and empty.
        public static InventoryBackupInfo Describe(byte[] data, IWorldAccessor world, bool parked = false)
        {
            var info = new InventoryBackupInfo { Parked = parked };
            if (data == null || data.Length == 0) return info;

            info.Present = true;
            try
            {
                var tree = new TreeAttribute();
                tree.FromBytes(data);

                info.SavedAtUnix = tree.GetLong("savedAt", 0);
                string code = tree.GetString("eventCode", "");
                info.EventCode = string.IsNullOrEmpty(code) ? null : code;
                info.KeepLiveInventory = tree.GetBool("keepLiveInventory", false);
                info.Health = tree.GetFloat("health", 0);
                info.MaxHealth = tree.GetFloat("maxHealth", 0);

                // Grouped by code, in order of first appearance.
                var order = new List<string>();
                var totals = new Dictionary<string, int>();
                var resolved = new Dictionary<string, bool>();

                int invCount = tree.GetInt("invCount", 0);
                for (int idx = 0; idx < invCount; idx++)
                {
                    if (!(tree[$"inv_{idx}"] is TreeAttribute invTree)) continue;
                    int slotCount = invTree.GetInt("slotCount", 0);

                    for (int i = 0; i < slotCount; i++)
                    {
                        var stack = invTree.GetItemstack($"slot_{i}");
                        if (stack == null) continue;
                        info.StackCount++;

                        bool ok = world != null && stack.ResolveBlockOrItem(world);
                        string itemCode = stack.Collectible?.Code?.ToString();
                        if (string.IsNullOrEmpty(itemCode))
                        {
                            // Unresolved: only class and id are left.
                            itemCode = (stack.Class == EnumItemClass.Block ? "block " : "item ")
                                + "#" + stack.Id;
                        }

                        if (!totals.ContainsKey(itemCode)) { order.Add(itemCode); totals[itemCode] = 0; }
                        totals[itemCode] += Math.Max(1, stack.StackSize);
                        resolved[itemCode] = ok;
                    }
                }

                foreach (var c in order) info.Items.Add((c, totals[c], resolved[c]));
            }
            catch (Exception)
            {
                // Present but unreadable; blob is left as is.
            }
            return info;
        }

        // Unlike Restore: no clear, and stacks are given rather than seated (the player has new items by now).
        public static (bool ok, int given, string message) RestoreParked(IServerPlayer player, ICoreServerAPI sapi,
            string byAdmin = null)
        {
            if (player == null || sapi == null) return (false, 0, "no such player online");

            byte[] data = player.GetModdata(FailedModDataKey);
            if (data == null || data.Length == 0)
                return (false, 0, player.PlayerName + " has no parked snapshot");

            var info = Describe(data, sapi.World, parked: true);
            if (info.KeepLiveInventory)
                return (false, 0, player.PlayerName + "'s parked snapshot is from an own-gear match; "
                    + "it is a record of what they carried, not a copy. Handing it over would duplicate "
                    + "everything that dropped when they died.");

            int given = 0;
            var tree = new TreeAttribute();
            // Slots already handed over, taken out of the blob if a later one throws.
            var handed = new HashSet<(int inv, int slot)>();
            try
            {
                tree.FromBytes(data);

                int invCount = tree.GetInt("invCount", 0);
                for (int idx = 0; idx < invCount; idx++)
                {
                    if (!(tree[$"inv_{idx}"] is TreeAttribute invTree)) continue;
                    int slotCount = invTree.GetInt("slotCount", 0);

                    for (int i = 0; i < slotCount; i++)
                    {
                        var stack = invTree.GetItemstack($"slot_{i}");
                        if (stack == null) continue;
                        if (!stack.ResolveBlockOrItem(sapi.World))
                        {
                            AuditAlways(sapi).Warning(
                                "parked restore {0}: '{1}' no longer resolves, skipped",
                                player.PlayerName, stack.Collectible?.Code?.ToString() ?? "<null>");
                            continue;
                        }
                        InventoryUtil.Give(sapi, player, stack, "parked backup");
                        handed.Add((idx, i));
                        given++;
                    }
                }
            }
            catch (Exception e)
            {
                // Keep the blob minus what was already given, so a retry gives only the rest.
                if (handed.Count > 0)
                {
                    try { StripSettled(tree, handed); player.SetModdata(FailedModDataKey, tree.ToBytes()); }
                    catch (Exception trimError)
                    {
                        AuditAlways(sapi).Error("parked restore for {0}: could not trim the {1} stack(s) "
                            + "already given; a retry would give them again: {2}",
                            player.PlayerName, handed.Count, trimError.Message);
                    }
                }
                AuditAlways(sapi).Error("parked restore for {0} failed after {1} stack(s): {2}",
                    player.PlayerName, given, e.Message);
                return (false, given, "failed after " + given + " stack(s): " + e.Message
                    + " - the snapshot is still parked.");
            }

            player.RemoveModdata(FailedModDataKey);

            AuditAlways(sapi).Notification("parked restore: {0} - {1} stack(s) handed back by {2}",
                player.PlayerName, given, byAdmin ?? "an admin");
            InventoryAuditLog.Record(sapi, InventoryAuditKind.ParkedRestored, player.PlayerUID,
                player.PlayerName, info.EventCode, given,
                "handed back by " + (byAdmin ?? "an admin"));

            return (true, given, "gave " + given + " stack(s) back to " + player.PlayerName
                + "; the parked snapshot is cleared.");
        }
    }
}
