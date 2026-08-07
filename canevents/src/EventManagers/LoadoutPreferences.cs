using System.Collections.Generic;
using System.Linq;
using canevents.src.Scoring;
using Vintagestory.API.Server;

namespace canevents.src.EventManagers
{
    /// <summary>The on-disk shape: { "Data": { playerUid: { eventCode: setName } } }. A wrapper object
    /// rather than a bare dictionary, because that is what existing files hold.</summary>
    public class LoadoutPrefsFile
    {
        public Dictionary<string, Dictionary<string, string>> Data { get; set; }
            = new Dictionary<string, Dictionary<string, string>>();
    }

    /// <summary>Per-player default loadout pick per event code, persisted in
    /// ModConfig/canevents/loadout_preferences.json.</summary>
    public class LoadoutPreferences : JsonFileStore<LoadoutPrefsFile>
    {
        public LoadoutPreferences(ICoreServerAPI sapi)
            : base(sapi, "loadout_preferences.json", "loadout_preferences.json") { }

        // An old or hand-emptied file deserializes to a null map; treat it as empty rather than
        // throwing on first use.
        private Dictionary<string, Dictionary<string, string>> Prefs
            => Data.Data ?? (Data.Data = new Dictionary<string, Dictionary<string, string>>());

        public string Get(string uid, string eventCode)
        {
            if (uid == null || eventCode == null) return null;
            lock (lockObj)
                return Prefs.TryGetValue(uid, out var byEvent) && byEvent.TryGetValue(eventCode, out var set) ? set : null;
        }

        public void Set(string uid, string eventCode, string setName)
        {
            if (uid == null || eventCode == null || setName == null) return;
            lock (lockObj)
            {
                if (!Prefs.TryGetValue(uid, out var byEvent))
                {
                    byEvent = new Dictionary<string, string>();
                    Prefs[uid] = byEvent;
                }
                byEvent[eventCode] = setName;
                Save();
            }
        }

        public void Clear(string uid, string eventCode)
        {
            if (uid == null || eventCode == null) return;
            lock (lockObj)
            {
                if (!Prefs.TryGetValue(uid, out var byEvent) || !byEvent.Remove(eventCode)) return;
                if (byEvent.Count == 0) Prefs.Remove(uid);
                Save();
            }
        }

        public List<(string eventCode, string setName)> GetAllForPlayer(string uid)
        {
            lock (lockObj)
            {
                if (uid == null || !Prefs.TryGetValue(uid, out var byEvent)) return new List<(string, string)>();
                return byEvent.Select(kv => (kv.Key, kv.Value)).ToList();
            }
        }
    }
}
