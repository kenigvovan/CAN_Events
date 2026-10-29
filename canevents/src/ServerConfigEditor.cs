using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using canevents.src.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace canevents.src
{
    // Wire value of ServerSettingRowMsg.Kind.
    public enum ServerSettingKind
    {
        Flag = 0,
        Int = 1,
        Double = 2,
        Text = 3,
        Choice = 4
    }

    // Admin panel editor for scalar keys of canevents.json. Lists have their own editors;
    // eventPlaceFolderName, disabledMutators and inventoryAudit are file-only.
    public sealed class ServerConfigEditor
    {
        private sealed class Setting
        {
            public string Key;
            public string Group;
            public ServerSettingKind Kind;
            public double Min, Max;
            public string[] Options;
            public bool Restart;
            public Func<string> Get;
            // Returns null or a lang key for the refusal.
            public Func<string, string> Set;
        }

        private readonly ICoreServerAPI sapi;
        private readonly Config config;
        private readonly List<Setting> settings = new List<Setting>();

        // Restart-only settings as they were at startup, to show "pending restart".
        private readonly Dictionary<string, string> startupValues = new Dictionary<string, string>();

        private canevents Mod => sapi.ModLoader.GetModSystem<canevents>();

        // Must be constructed before anything edits the config (takes the startup snapshot).
        public ServerConfigEditor(ICoreServerAPI sapi, Config config)
        {
            this.sapi = sapi;
            this.config = config;
            Define();
            foreach (var s in settings)
                if (s.Restart) startupValues[s.Key] = s.Get();
        }

        private void Define()
        {
            var c = config;

            const string schedule = "schedule";
            // Scheduler keeps its own copy and resets its countdown.
            Flag("autoScheduleEnabled", schedule, false, () => c.autoScheduleEnabled, v =>
            {
                var sch = Mod?.eventScheduler;
                if (sch != null) sch.SetEnabled(v); else c.autoScheduleEnabled = v;
            });
            Int("scheduleIntervalMinutes", schedule, false, 1, 24 * 60, () => c.scheduleIntervalMinutes, v =>
            {
                var sch = Mod?.eventScheduler;
                if (sch != null) sch.SetInterval(v); else c.scheduleIntervalMinutes = v;
            });
            Int("minPlayersToStart", schedule, false, 0, 1000, () => c.minPlayersToStart, v => c.minPlayersToStart = v);
            Int("maxConcurrentEvents", schedule, false, 1, 100, () => c.maxConcurrentEvents, v => c.maxConcurrentEvents = v);
            Int("signupWindowMinutes", schedule, false, 0, 24 * 60, () => c.signupWindowMinutes, v => c.signupWindowMinutes = v);

            const string match = "match";
            Int("countdownSeconds", match, false, 0, 300, () => c.countdownSeconds, v => c.countdownSeconds = v);
            Int("maxEventDurationSeconds", match, false, 10, 24 * 3600, () => c.maxEventDurationSeconds, v => c.maxEventDurationSeconds = v);
            Int("queueReadyCheckSeconds", match, false, 0, 600, () => c.queueReadyCheckSeconds, v => c.queueReadyCheckSeconds = v);
            Int("maxLocationSearchSeconds", match, false, 0, 24 * 3600, () => c.maxLocationSearchSeconds, v => c.maxLocationSearchSeconds = v);
            Flag("allowFriendlyFire", match, false, () => c.allowFriendlyFire, v => c.allowFriendlyFire = v);
            // Parsed once into CANEventManager.IssuedGearDefault.
            Choice("issuedGearDrop", match, true, new[] { "cleanup", "return", "remove", "allow" },
                () => c.issuedGearDrop ?? "cleanup", v => c.issuedGearDrop = v);

            const string queue = "queue";
            // Re-register timers so the search timeout ages at the real tick rate.
            Int("attemptComposeTeamsEveryNSeconds", queue, false, 1, 60, () => c.attemptComposeTeamsEveryNSeconds, v =>
            {
                c.attemptComposeTeamsEveryNSeconds = v;
                Mod?.CANEventManager?.ApplyTickIntervals();
            });
            Int("attemptFindLocationForRunningEventEveryNSeconds", queue, false, 1, 60,
                () => c.attemptFindLocationForRunningEventEveryNSeconds, v =>
                {
                    c.attemptFindLocationForRunningEventEveryNSeconds = v;
                    Mod?.CANEventManager?.ApplyTickIntervals();
                });
            // Handed to the ranked queue managers' constructors.
            Int("rankedMmrBaseTolerance", queue, true, 0, 5000, () => c.rankedMmrBaseTolerance, v => c.rankedMmrBaseTolerance = v);
            Int("rankedMmrToleranceWidenPerSecond", queue, true, 0, 1000, () => c.rankedMmrToleranceWidenPerSecond, v => c.rankedMmrToleranceWidenPerSecond = v);

            const string duels = "duels";
            Flag("duelsEnabled", duels, false, () => c.duelsEnabled, v => c.duelsEnabled = v);
            Int("duelInviteTimeoutSeconds", duels, false, 10, 600, () => c.duelInviteTimeoutSeconds, v => c.duelInviteTimeoutSeconds = v);

            const string spectate = "spectate";
            Flag("autoSpectateAdmins", spectate, false, () => c.autoSpectateAdmins, v => c.autoSpectateAdmins = v);
            Int("spectatorTelemetryIntervalMs", spectate, false, 100, 5000, () => c.spectatorTelemetryIntervalMs, v => c.spectatorTelemetryIntervalMs = v);

            const string arena = "arena";
            Flag("pinArenaChunks", arena, false, () => c.pinArenaChunks, v => c.pinArenaChunks = v);
            Flag("arenaOutlineParticles", arena, false, () => c.arenaOutlineParticles, v => c.arenaOutlineParticles = v);
            Dbl("arenaOutlineIntervalSeconds", arena, false, 0.25, 10, () => c.arenaOutlineIntervalSeconds, v => c.arenaOutlineIntervalSeconds = v);
            Int("maxPlatformFloodFillRadius", arena, false, 1, 200, () => c.maxPlatformFloodFillRadius, v => c.maxPlatformFloodFillRadius = v);
            Int("maxPlatformFloodFillBlocks", arena, false, 1, 100000, () => c.maxPlatformFloodFillBlocks, v => c.maxPlatformFloodFillBlocks = v);

            const string clients = "clients";
            // Sent in the join packet, applies on next login.
            Text("mainDialogHotkey", clients, false, () => c.mainDialogHotkey, ValidateHotkey, v =>
            {
                c.mainDialogHotkey = v;
                var net = Mod?.ServerNetworkHandler;
                if (net != null) net.MainDialogHotkey = v;
            });
            Flag("diagnosticsLogging", clients, false, () => c.diagnosticsLogging, v =>
            {
                c.diagnosticsLogging = v;
                var net = Mod?.ServerNetworkHandler;
                if (net != null) net.DiagnosticsLogging = v;
            });
            // InventoryAuditLog.Configure runs once at start.
            Int("inventoryAuditMaxEntries", clients, true, 100, 100000, () => c.inventoryAuditMaxEntries, v => c.inventoryAuditMaxEntries = v);
        }

        private void Flag(string key, string group, bool restart, Func<bool> get, Action<bool> set)
            => settings.Add(new Setting
            {
                Key = key, Group = group, Kind = ServerSettingKind.Flag, Restart = restart,
                Get = () => get() ? "true" : "false",
                Set = text => { set(text == "true"); return null; }
            });

        private void Int(string key, string group, bool restart, int min, int max, Func<int> get, Action<int> set)
            => settings.Add(new Setting
            {
                Key = key, Group = group, Kind = ServerSettingKind.Int, Restart = restart, Min = min, Max = max,
                Get = () => get().ToString(CultureInfo.InvariantCulture),
                Set = text => { set(int.Parse(text, CultureInfo.InvariantCulture)); return null; }
            });

        private void Dbl(string key, string group, bool restart, double min, double max, Func<double> get, Action<double> set)
            => settings.Add(new Setting
            {
                Key = key, Group = group, Kind = ServerSettingKind.Double, Restart = restart, Min = min, Max = max,
                Get = () => get().ToString("0.###", CultureInfo.InvariantCulture),
                Set = text => { set(double.Parse(text, CultureInfo.InvariantCulture)); return null; }
            });

        private void Text(string key, string group, bool restart, Func<string> get, Func<string, string> validate, Action<string> set)
            => settings.Add(new Setting
            {
                Key = key, Group = group, Kind = ServerSettingKind.Text, Restart = restart,
                Get = () => get() ?? "",
                Set = text =>
                {
                    string error = validate(text);
                    if (error != null) return error;
                    set(text);
                    return null;
                }
            });

        private void Choice(string key, string group, bool restart, string[] options, Func<string> get, Action<string> set)
            => settings.Add(new Setting
            {
                Key = key, Group = group, Kind = ServerSettingKind.Choice, Restart = restart, Options = options,
                Get = () => get() ?? "",
                Set = text => { set(text); return null; }
            });

        // Same parse as the client; Enum.TryParse would accept plain numbers.
        private static string ValidateHotkey(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || int.TryParse(text, out _)
                || !Enum.TryParse<GlKeys>(text, true, out _))
                return "canevents:admin-srv-bad-hotkey";
            return null;
        }

        public ServerSettingsMsg BuildMsg()
        {
            return new ServerSettingsMsg
            {
                Rows = settings.Select(s =>
                {
                    string value = s.Get();
                    return new ServerSettingRowMsg
                    {
                        Key = s.Key,
                        GroupLangKey = "canevents:admin-srv-group-" + s.Group,
                        LabelLangKey = LabelKey(s.Key),
                        DescLangKey = LabelKey(s.Key) + "-desc",
                        Kind = (int)s.Kind,
                        Value = value,
                        Min = s.Min,
                        Max = s.Max,
                        Options = s.Options,
                        RequiresRestart = s.Restart,
                        PendingRestart = s.Restart && startupValues.TryGetValue(s.Key, out var started)
                                         && !string.Equals(started, value, StringComparison.Ordinal)
                    };
                }).ToArray()
            };
        }

        private static string LabelKey(string key) => "canevents:admin-srv-" + key.ToLowerInvariant();

        // Out-of-range values are refused, never clamped.
        public bool TryApply(IServerPlayer player, string key, string rawValue, out string message)
        {
            var s = settings.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.Ordinal));
            if (s == null)
            {
                message = Lang.Get("canevents:admin-srv-unknown", key ?? "");
                return false;
            }

            string label = Lang.Get(LabelKey(s.Key));
            if (!TryNormalize(s, (rawValue ?? "").Trim(), out string value, out string errorKey))
            {
                message = errorKey == "canevents:admin-srv-out-of-range"
                    ? Lang.Get(errorKey, label, Format(s, s.Min), Format(s, s.Max))
                    : Lang.Get(errorKey, label);
                return false;
            }

            string before = s.Get();
            string refused = s.Set(value);
            if (refused != null)
            {
                message = Lang.Get(refused, label);
                return false;
            }

            // Save via the admin store so its lists aren't overwritten with stale copies.
            var store = Mod?.CANEventManager?.AdminState;
            if (store != null) store.Save();
            else AdminStateStore.SaveConfig(sapi, config);

            string after = s.Get();
            sapi.Logger.Notification("[canevents] {0} set server setting {1}: {2} -> {3}{4}",
                player?.PlayerName ?? "?", s.Key, before, after, s.Restart ? " (applies after restart)" : "");

            message = Lang.Get(s.Restart ? "canevents:admin-srv-saved-restart" : "canevents:admin-srv-saved",
                label, after);
            return true;
        }

        private static string Format(Setting s, double v)
            => s.Kind == ServerSettingKind.Int
                ? ((int)v).ToString(CultureInfo.InvariantCulture)
                : v.ToString("0.###", CultureInfo.InvariantCulture);

        private static bool TryNormalize(Setting s, string text, out string value, out string errorKey)
        {
            value = null;
            errorKey = null;
            switch (s.Kind)
            {
                case ServerSettingKind.Flag:
                    switch (text.ToLowerInvariant())
                    {
                        case "true": case "1": case "on": case "yes": value = "true"; return true;
                        case "false": case "0": case "off": case "no": value = "false"; return true;
                    }
                    errorKey = "canevents:admin-srv-not-a-flag";
                    return false;

                case ServerSettingKind.Int:
                    if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i))
                    {
                        errorKey = "canevents:admin-srv-not-a-number";
                        return false;
                    }
                    if (i < s.Min || i > s.Max)
                    {
                        errorKey = "canevents:admin-srv-out-of-range";
                        return false;
                    }
                    value = i.ToString(CultureInfo.InvariantCulture);
                    return true;

                case ServerSettingKind.Double:
                    // Accept a decimal comma.
                    if (!double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
                        || double.IsNaN(d) || double.IsInfinity(d))
                    {
                        errorKey = "canevents:admin-srv-not-a-number";
                        return false;
                    }
                    if (d < s.Min || d > s.Max)
                    {
                        errorKey = "canevents:admin-srv-out-of-range";
                        return false;
                    }
                    value = d.ToString("R", CultureInfo.InvariantCulture);
                    return true;

                case ServerSettingKind.Choice:
                    value = s.Options.FirstOrDefault(o => string.Equals(o, text, StringComparison.OrdinalIgnoreCase));
                    if (value != null) return true;
                    errorKey = "canevents:admin-srv-not-an-option";
                    return false;

                case ServerSettingKind.Text:
                    if (text.Length > 64)
                    {
                        errorKey = "canevents:admin-srv-too-long";
                        return false;
                    }
                    value = text;
                    return true;
            }
            errorKey = "canevents:admin-srv-unknown";
            return false;
        }
    }
}
