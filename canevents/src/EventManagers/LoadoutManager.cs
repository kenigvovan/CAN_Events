using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace canevents.src.EventManagers
{
    public class LoadoutManager
    {
        public class LoadoutEntry
        {
            public string Code { get; set; }
            public int Quantity { get; set; }

            // Loadouts only; outfits leave both empty.
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public string Id { get; set; }
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public List<eventBehavior.Shared.UpgradeStep> Upgrades { get; set; }
        }

        public class LoadoutSet
        {
            public string Name { get; set; }
            public List<LoadoutEntry> Items { get; set; } = new List<LoadoutEntry>();
        }

        private class LoadoutFile
        {
            public List<LoadoutSet> Sets { get; set; } = new List<LoadoutSet>();
        }

        private readonly string folder;
        private readonly string subFolder;
        private readonly Dictionary<string, List<LoadoutSet>> loadouts = new Dictionary<string, List<LoadoutSet>>();
        private readonly object lockObj = new object();
        private readonly Vintagestory.API.Common.ILogger logger;

        // One store per folder under ModConfig/canevents/; outfits use the same shape (see ItemSetKinds).
        public LoadoutManager(string modConfigBase, Vintagestory.API.Common.ILogger logger = null,
            string subFolder = "loadouts")
        {
            this.subFolder = subFolder;
            folder = Path.Combine(modConfigBase, subFolder);
            Directory.CreateDirectory(folder);
            this.logger = logger;
        }

        // Event codes are used as filenames; reject anything that isn't a safe identifier, to prevent
        // path traversal.
        public static bool IsValidEventCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return false;
            if (code.Length > 64) return false;
            foreach (var c in code)
            {
                if (!(char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.')) return false;
            }
            // Defense-in-depth: forbid ".." segments and a bare dot.
            if (code == "." || code == ".." || code.Contains("..")) return false;
            return true;
        }

        public void Load(IEnumerable<string> eventCodes)
        {
            lock (lockObj)
            {
                foreach (var code in eventCodes)
                {
                    if (!IsValidEventCode(code))
                    {
                        logger?.Warning("[canevents] Skipping load of loadouts for invalid event code '{0}'", code);
                        continue;
                    }
                    var file = Path.Combine(folder, code + ".json");
                    if (File.Exists(file))
                    {
                        try
                        {
                            var data = JsonConvert.DeserializeObject<LoadoutFile>(File.ReadAllText(file));
                            loadouts[code] = data?.Sets ?? new List<LoadoutSet>();
                        }
                        catch (Exception ex)
                        {
                            logger?.Error("[canevents] Failed to load loadouts for '{0}': {1}", code, ex.Message);
                            loadouts[code] = new List<LoadoutSet>();
                        }
                    }
                    else
                    {
                        // No managed sets: stay empty so inline JSON sets act as the fallback. The file
                        // is created lazily on first AddSet.
                        loadouts[code] = new List<LoadoutSet>();
                    }
                }
            }
        }

        public List<LoadoutSet> GetSets(string eventCode)
        {
            lock (lockObj)
            {
                return loadouts.TryGetValue(eventCode, out var s)
                    ? new List<LoadoutSet>(s)
                    : new List<LoadoutSet>();
            }
        }

        public Dictionary<string, List<LoadoutSet>> GetAll()
        {
            lock (lockObj)
            {
                return loadouts.ToDictionary(kvp => kvp.Key, kvp => new List<LoadoutSet>(kvp.Value));
            }
        }

        public void AddSet(string eventCode, LoadoutSet set)
        {
            if (!IsValidEventCode(eventCode)) throw new ArgumentException("Invalid event code", nameof(eventCode));
            lock (lockObj)
            {
                if (!loadouts.ContainsKey(eventCode))
                    loadouts[eventCode] = new List<LoadoutSet>();
                loadouts[eventCode].RemoveAll(s => s.Name.Equals(set.Name, StringComparison.OrdinalIgnoreCase));
                loadouts[eventCode].Add(set);
                SaveToFileUnlocked(eventCode);
            }
        }

        public bool DeleteSet(string eventCode, string setName)
        {
            if (!IsValidEventCode(eventCode)) return false;
            lock (lockObj)
            {
                if (!loadouts.TryGetValue(eventCode, out var sets)) return false;
                int n = sets.RemoveAll(s => s.Name.Equals(setName, StringComparison.OrdinalIgnoreCase));
                if (n > 0) SaveToFileUnlocked(eventCode);
                return n > 0;
            }
        }

        public int AddSets(string eventCode, IEnumerable<LoadoutSet> sets)
        {
            if (!IsValidEventCode(eventCode)) throw new ArgumentException("Invalid event code", nameof(eventCode));
            lock (lockObj)
            {
                if (!loadouts.ContainsKey(eventCode))
                    loadouts[eventCode] = new List<LoadoutSet>();
                var list = loadouts[eventCode];
                int added = 0;
                foreach (var set in sets)
                {
                    if (list.Any(s => s.Name.Equals(set.Name, StringComparison.OrdinalIgnoreCase))) continue;
                    list.Add(set);
                    added++;
                }
                if (added > 0) SaveToFileUnlocked(eventCode);
                return added;
            }
        }

        private void SaveToFileUnlocked(string eventCode)
        {
            var file = Path.Combine(folder, eventCode + ".json");
            var data = new LoadoutFile { Sets = loadouts.TryGetValue(eventCode, out var s) ? s : new List<LoadoutSet>() };
            Scoring.JsonStore.WriteAtomic(file, data, logger, subFolder + "/" + eventCode + ".json");
        }

    }
}
