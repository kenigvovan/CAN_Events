using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace canevents.src
{
    /// <summary>
    /// Admin toggles that are state rather than settings: disabled events and queue-barred players.
    /// The set is the state and saving is one call, so a toggle can't silently revert on restart.
    /// </summary>
    public class AdminStateStore
    {
        private readonly ICoreServerAPI sapi;
        private readonly Config config;

        /// <summary>Event codes disabled via the admin GUI. Case-insensitive, like every code compare.</summary>
        public HashSet<string> DisabledEventCodes { get; }

        /// <summary>Player UIDs barred from every queue. Case-sensitive: UIDs are opaque ids, not names.</summary>
        public HashSet<string> BlacklistedUids { get; }

        public AdminStateStore(ICoreServerAPI sapi, Config config)
        {
            this.sapi = sapi;
            this.config = config;

            DisabledEventCodes = new HashSet<string>(
                config?.disabledEventCodes ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            BlacklistedUids = new HashSet<string>(
                config?.queueBlacklistedUids ?? Array.Empty<string>(), StringComparer.Ordinal);
        }

        public bool IsEventEnabled(string code)
            => string.IsNullOrEmpty(code) || !DisabledEventCodes.Contains(code);

        public bool IsPlayerBlacklisted(string uid)
            => !string.IsNullOrEmpty(uid) && BlacklistedUids.Contains(uid);

        /// <summary>Returns whether the set changed; the caller decides what that implies.</summary>
        public bool SetEventEnabled(string code, bool enabled)
        {
            if (string.IsNullOrEmpty(code)) return false;
            bool changed = enabled ? DisabledEventCodes.Remove(code) : DisabledEventCodes.Add(code);
            if (changed) Save();
            return changed;
        }

        public bool SetPlayerBlacklisted(string uid, bool blacklisted)
        {
            if (string.IsNullOrEmpty(uid)) return false;
            bool changed = blacklisted ? BlacklistedUids.Add(uid) : BlacklistedUids.Remove(uid);
            if (changed) Save();
            return changed;
        }

        /// <summary>Mirrors the live sets into the config and writes it out. Public so other persisted
        /// admin toggles share the one save path.</summary>
        public void Save()
        {
            if (config == null) return;
            config.disabledEventCodes = DisabledEventCodes.ToArray();
            config.queueBlacklistedUids = BlacklistedUids.ToArray();
            SaveConfig(sapi, config);
        }

        /// <summary>The single "write the mod config" path; the file name is derived in one place.</summary>
        public static void SaveConfig(ICoreAPI api, Config config)
        {
            if (api == null || config == null) return;
            api.StoreModConfig(config, ConfigFileName(api));
        }

        public static string ConfigFileName(ICoreAPI api)
            => (api.ModLoader.GetModSystem<canevents>()?.Mod?.Info?.ModID ?? "canevents") + ".json";
    }
}
