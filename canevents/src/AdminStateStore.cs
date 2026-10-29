using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace canevents.src
{
    // Disabled events and queue blacklist, persisted to the mod config.
    public class AdminStateStore
    {
        private readonly ICoreServerAPI sapi;
        private readonly Config config;

        public HashSet<string> DisabledEventCodes { get; }

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

        // Use this instead of SaveConfig so the sets get copied into the config first.
        public void Save()
        {
            if (config == null) return;
            config.disabledEventCodes = DisabledEventCodes.ToArray();
            config.queueBlacklistedUids = BlacklistedUids.ToArray();
            SaveConfig(sapi, config);
        }

        public static void SaveConfig(ICoreAPI api, Config config)
        {
            if (api == null || config == null) return;
            api.StoreModConfig(config, ConfigFileName(api));
        }

        public static string ConfigFileName(ICoreAPI api)
            => (api.ModLoader.GetModSystem<canevents>()?.Mod?.Info?.ModID ?? "canevents") + ".json";
    }
}
