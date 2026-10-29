using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using canevents.src.EventManagers;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using static canevents.src.commands.CommandArgs;

namespace canevents.src.commands
{
    // Inventory snapshots live in ModData, which nothing else can show on a running server.
    // Read-only except restore.
    public class InventoryBackupCommandsImpl
    {
        private readonly ICoreServerAPI sapi;

        public InventoryBackupCommandsImpl(ICoreServerAPI sapi) { this.sapi = sapi; }

        private CANEventManager Events
            => sapi.ModLoader.GetModSystem<canevents>()?.CANEventManager;

        public TextCommandResult Status(TextCommandCallingArgs args)
        {
            string typed = Str(args, 0);
            string uid = PlayerModDataAccess.UidOfName(sapi, typed);
            if (uid == null) return TextCommandResult.Error("no player known by the name '" + typed + "'");

            var sb = new StringBuilder();
            sb.Append(Describe(uid, PlayerModDataAccess.NameOf(sapi, uid), verbose: true));
            return TextCommandResult.Success(sb.ToString());
        }

        public TextCommandResult List(TextCommandCallingArgs args)
        {
            string typed = Str(args, 0);
            string uid = PlayerModDataAccess.UidOfName(sapi, typed);
            if (uid == null) return TextCommandResult.Error("no player known by the name '" + typed + "'");

            string name = PlayerModDataAccess.NameOf(sapi, uid);
            bool wantParked = string.Equals(Str(args, 1), "parked", StringComparison.OrdinalIgnoreCase);

            var info = Read(uid, wantParked, out _);
            if (!info.Present)
                return TextCommandResult.Error(name + " has no "
                    + (wantParked ? "parked" : "active") + " snapshot"
                    + (wantParked ? "" : " - try 'parked' as the second argument"));

            if (info.Items.Count == 0)
                return TextCommandResult.Success(name + "'s snapshot holds nothing readable "
                    + "(it may be corrupt, or it was taken from an empty inventory).");

            var sb = new StringBuilder();
            sb.AppendLine(Headline(name, info));
            foreach (var (code, qty, resolved) in info.Items)
                sb.AppendLine("  " + qty + " x " + code + (resolved ? "" : "   <- no longer exists"));
            return TextCommandResult.Success(sb.ToString().TrimEnd());
        }

        public TextCommandResult Restore(TextCommandCallingArgs args)
        {
            string typed = Str(args, 0);
            string uid = PlayerModDataAccess.UidOfName(sapi, typed);
            if (uid == null) return TextCommandResult.Error("no player known by the name '" + typed + "'");

            if (!InventorySnapshots.TryOnlineForRestore(sapi, uid, out var player, out var offline))
                return TextCommandResult.Error(offline);

            string admin = (args.Caller.Player as IServerPlayer)?.PlayerName;
            var (ok, _, message) = PlayerInventoryBackup.RestoreParked(player, sapi, admin);
            return ok ? TextCommandResult.Success(message) : TextCommandResult.Error(message);
        }

        public TextCommandResult Scan(TextCommandCallingArgs args)
        {
            var rows = new List<string>();
            int checkedCount = 0, unreadable = 0;
            // Over the save-file read budget, players are reported as unchecked (see Sweep).
            var sweep = new InventorySnapshots.Sweep(sapi);

            foreach (var (uid, name) in PlayerModDataAccess.KnownPlayers(sapi))
            {
                checkedCount++;

                var pair = sweep.Read(uid);
                if (pair.Unreadable)
                {
                    unreadable++;
                    continue;
                }
                if (!pair.HoldsAnything) continue;

                var flags = new List<string>();
                if (pair.Active.Present) flags.Add("active " + Summary(pair.Active));
                if (pair.Parked.Present) flags.Add("PARKED " + Summary(pair.Parked));
                if (InLiveMatch(uid)) flags.Add("in a match right now");

                rows.Add(name + ": " + string.Join(", ", flags));
            }

            string tail = unreadable > 0
                ? ", " + unreadable + " not checked (long-absent accounts; ask about one by name with "
                  + "/eventadmin inv status <player>)"
                : "";

            if (rows.Count == 0)
                return TextCommandResult.Success("Nobody is holding an unsettled inventory snapshot ("
                    + checkedCount + " player(s) checked" + tail + ").");

            var sb = new StringBuilder();
            sb.AppendLine(rows.Count + " player(s) with an unsettled snapshot, of " + checkedCount
                + " checked" + tail + ":");
            foreach (var r in rows) sb.AppendLine("  " + r);
            sb.Append("A PARKED snapshot is one whose restore failed - those items are with nobody. "
                + "Hand one back with /eventadmin inv restore <player>.");
            return TextCommandResult.Success(sb.ToString());
        }

        public TextCommandResult History(TextCommandCallingArgs args)
        {
            if (!InventoryAuditLog.Enabled)
                return TextCommandResult.Error("the inventory audit is off (config: inventoryAudit). "
                    + "Nothing has been recorded since it was disabled.");

            string typed = Str(args, 0);
            string uid = null;
            if (!string.IsNullOrWhiteSpace(typed))
            {
                uid = PlayerModDataAccess.UidOfName(sapi, typed);
                if (uid == null) return TextCommandResult.Error("no player known by the name '" + typed + "'");
            }

            int typedLimit = Num(args, 1);
            int limit = Math.Clamp(typedLimit > 0 ? typedLimit : 20, 1, 200);
            var entries = InventoryAuditLog.Recent(limit, uid);
            if (entries.Count == 0)
                return TextCommandResult.Success(uid == null
                    ? "The audit history is empty."
                    : "Nothing recorded for " + PlayerModDataAccess.NameOf(sapi, uid) + ".");

            var sb = new StringBuilder();
            sb.AppendLine("Last " + entries.Count + " entr(ies), newest first:");
            foreach (var e in entries)
            {
                sb.AppendLine("  " + TimeText.Duration(UnixTime.Now - e.AtUnix) + " ago  "
                    + KindText(e.Kind) + "  " + (e.PlayerName ?? e.PlayerUid)
                    + (string.IsNullOrEmpty(e.EventCode) ? "" : "  [" + e.EventCode + "]")
                    + (e.StackCount > 0 ? "  " + e.StackCount + " stack(s)" : "")
                    + (string.IsNullOrEmpty(e.Detail) ? "" : "  - " + e.Detail));
            }
            return TextCommandResult.Success(sb.ToString().TrimEnd());
        }

        public TextCommandResult Gui(TextCommandCallingArgs args)
        {
            if (!(args.Caller.Player is IServerPlayer player))
                return TextCommandResult.Error("players only - the console has no screen to open it on");

            var handler = sapi.ModLoader.GetModSystem<canevents>()?.ServerNetworkHandler;
            if (handler == null) return TextCommandResult.Error("the network handler is not up");

            handler.OpenInventoryAuditFor(player);
            return TextCommandResult.Success("Opening the inventory audit window.");
        }

        private InventoryBackupInfo Read(string uid, bool parked, out PlayerModDataAccess.Source source)
            => InventorySnapshots.Read(sapi, uid, parked, out source);

        private string Describe(string uid, string name, bool verbose)
        {
            var active = Read(uid, parked: false, out var source);
            var parked = Read(uid, parked: true, out _);

            var sb = new StringBuilder();
            sb.AppendLine(name + " (" + SourceText(source) + ")"
                + (InLiveMatch(uid) ? " - in a match right now" : ""));

            sb.AppendLine(active.Present
                ? "  active snapshot: " + Summary(active)
                : "  active snapshot: none - nothing of theirs is being held");

            if (parked.Present)
                sb.AppendLine("  PARKED snapshot: " + Summary(parked)
                    + "  <- a restore failed; these items are with nobody. "
                    + "Give them back with /eventadmin inv restore " + name);

            if (verbose && active.Present && active.MaxHealth > 0)
                sb.AppendLine("  health at the time: " + Math.Round(active.Health, 1)
                    + " / " + Math.Round(active.MaxHealth, 1));

            if (verbose)
                sb.Append("  see the items with /eventadmin inv list " + name
                    + (parked.Present ? " [parked]" : ""));

            return sb.ToString().TrimEnd();
        }

        private static string Summary(InventoryBackupInfo info)
        {
            var parts = new List<string> { info.StackCount + " stack(s)" };
            if (!string.IsNullOrEmpty(info.EventCode)) parts.Add("event '" + info.EventCode + "'");
            parts.Add(info.AgeSeconds >= 0
                ? "taken " + TimeText.Duration(info.AgeSeconds) + " ago"
                : "taken before snapshots were dated");
            if (info.KeepLiveInventory) parts.Add("own-gear match: a record, never given back");
            return string.Join(", ", parts);
        }

        private static string Headline(string name, InventoryBackupInfo info)
            => name + "'s " + (info.Parked ? "PARKED" : "active") + " snapshot - " + Summary(info) + ":";

        private bool InLiveMatch(string uid) => Events?.FindLiveEventOf(uid) != null;

        private static string SourceText(PlayerModDataAccess.Source source) => source switch
        {
            PlayerModDataAccess.Source.Online => "online",
            PlayerModDataAccess.Source.Memory => "offline, read from memory",
            PlayerModDataAccess.Source.Database => "offline, read from the save file",
            _ => "offline, could not be read"
        };

        private static string KindText(InventoryAuditKind kind) => kind switch
        {
            InventoryAuditKind.Taken => "TAKEN   ",
            InventoryAuditKind.Returned => "RETURNED",
            InventoryAuditKind.RestoreFailed => "FAILED  ",
            InventoryAuditKind.RefusedOverwrite => "REFUSED ",
            InventoryAuditKind.ParkedRestored => "BY ADMIN",
            _ => kind.ToString()
        };
    }
}
