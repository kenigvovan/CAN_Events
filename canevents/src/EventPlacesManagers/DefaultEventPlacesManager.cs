using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using canevents.src.EventPlacesManagers.Places;
using Newtonsoft.Json;
using Vintagestory.API.Common;

namespace canevents.src.EventPlacesManagers
{
    public class DefaultEventPlacesManager
    {
        public HashSet<EventPlace> EventPlaces { get; set; }
        public long LastId = 0;
        string pathToJsonFiles;
        readonly ILogger logger;

        public DefaultEventPlacesManager(string pathToJson, ILogger logger = null)
        {
            this.EventPlaces = new HashSet<EventPlace>();
            this.pathToJsonFiles = pathToJson;
            this.logger = logger;
        }

        void Log(string message) => logger?.Notification("[canevents] " + message);
        void LogWarn(string message) => logger?.Warning("[canevents] " + message);
        // Persisted so a deleted arena's id is never reused (tournament whitelists, rollback files).
        // Not .json, so the place loader skips it.
        string IdCounterPath => Path.Combine(pathToJsonFiles, "next_place_id.txt");

        long ReadIdCounter()
        {
            try
            {
                if (File.Exists(IdCounterPath) && long.TryParse(File.ReadAllText(IdCounterPath).Trim(), out long next))
                    return next;
            }
            catch (Exception e)
            {
                LogWarn("could not read the place id counter, rebuilding it from the files: " + e.Message);
            }
            return 0;
        }

        void SaveIdCounter()
        {
            if (!Scoring.JsonStore.TryWriteAtomic(IdCounterPath, LastId, out var error))
                LogWarn("could not save the place id counter: " + error);
        }

        public void LoadEventPlaces()
        {
            var duplicates = new List<(EventPlace place, string file)>();
            var seenIds = new HashSet<long>();

            if (Directory.Exists(pathToJsonFiles))
            {
                foreach (var eventPlaceFile in Directory.GetFiles(pathToJsonFiles, "*.json"))
                {
                    EventPlace eventPlace;
                    try
                    {
                        eventPlace = JsonConvert.DeserializeObject<EventPlace>(File.ReadAllText(eventPlaceFile));
                    }
                    catch (Exception e)
                    {
                        logger?.Error("[canevents] LoadEventPlaces: {0} could not be read, skipping it: {1}",
                            Path.GetFileName(eventPlaceFile), e.Message);
                        continue;
                    }
                    if (eventPlace == null)
                    {
                        LogWarn(string.Format("LoadEventPlaces: {0} deserialized to nothing, skipping it.",
                            Path.GetFileName(eventPlaceFile)));
                        continue;
                    }

                    // HasBounds survives a round-trip, the corners may not.
                    if (eventPlace.HasBounds && (eventPlace.BoundsMin == null || eventPlace.BoundsMax == null))
                    {
                        LogWarn(string.Format(
                            "LoadEventPlaces: {0}: HasBounds was true but a corner is null - clearing the flag.",
                            Path.GetFileName(eventPlaceFile)));
                        eventPlace.HasBounds = false;
                    }

                    // Duplicate id: renumbered below once the max id is known.
                    if (!seenIds.Add(eventPlace.Id))
                    {
                        duplicates.Add((eventPlace, eventPlaceFile));
                        continue;
                    }

                    this.EventPlaces.Add(eventPlace);
                    if (eventPlace.Id >= LastId)
                    {
                        LastId = eventPlace.Id + 1;
                    }
                    int bdCount = eventPlace.BehaviorData?.Count ?? -1;
                    bool hasParkour = eventPlace.BehaviorData?.ContainsKey(BehaviorKeys.Parkour) ?? false;
                    Log(string.Format(
                        "LoadEventPlaces: file={0} id={1} name={2} types={3} BehaviorData.Count={4} hasParkour={5} HasBounds={6} min={7} max={8} spawns={9}",
                        Path.GetFileName(eventPlaceFile), eventPlace.Id, eventPlace.Name,
                        eventPlace.placeTypes != null ? string.Join(",", eventPlace.placeTypes) : "(null)",
                        bdCount, hasParkour,
                        eventPlace.HasBounds,
                        eventPlace.BoundsMin?.ToString() ?? "(null)",
                        eventPlace.BoundsMax?.ToString() ?? "(null)",
                        eventPlace.spawnPositions?.Count ?? -1));
                }
            }
            else
            {
                Directory.CreateDirectory(pathToJsonFiles);
            }

            LastId = Math.Max(LastId, ReadIdCounter());

            foreach (var (place, file) in duplicates)
            {
                long oldId = place.Id;
                place.Id = LastId++;
                this.EventPlaces.Add(place);
                string target = Path.Combine(pathToJsonFiles, FileNameFor(place));
                LogWarn(string.Format("LoadEventPlaces: {0} has id {1}, which another place already has - "
                    + "renumbered to {2}.", Path.GetFileName(file), oldId, place.Id));
                if (SavePlace(place) && !string.Equals(file, target, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(file); }
                    catch (Exception e) { LogWarn("could not remove " + Path.GetFileName(file) + ": " + e.Message); }
                }
            }

            SaveIdCounter();
        }
        private static readonly Random _random = new Random();

        // Random free arena for these tags. Empty allowedIds means no whitelist.
        public bool TryFindEventPlace(string[] searchTypes, out EventPlace result, ICollection<long> allowedIds = null)
        {
            bool restricted = allowedIds != null && allowedIds.Count > 0;
            var candidates = new List<EventPlace>();
            foreach (var item in this.EventPlaces)
            {
                if (restricted && !allowedIds.Contains(item.Id)) continue;
                if (item.IsAvailableFor(searchTypes)) candidates.Add(item);
            }

            if (candidates.Count > 0)
            {
                result = candidates[_random.Next(candidates.Count)];
                return true;
            }

            result = null;
            return false;
        }

        public EventPlace FindById(long id) => EventPlaces.FirstOrDefault(it => it.Id == id);

        public bool DeletePlace(long id)
        {
            EventPlace found = FindById(id);
            if (found != null)
            {
                this.EventPlaces.RemoveWhere(it => it.Id == id);

                try
                {
                    var filePath = Path.Combine(this.pathToJsonFiles, FileNameFor(found));
                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                    }
                    // In case the file was renamed.
                    foreach (var file in Directory.GetFiles(this.pathToJsonFiles, "*_" + found.Id + ".json"))
                    {
                        File.Delete(file);
                    }
                }
                catch (Exception e)
                {
                    LogWarn("deleted place " + found.Name + " (#" + found.Id
                        + ") but could not remove its file: " + e.Message);
                }

                return true;
            }
            return false;
        }
        public bool SavePlace(EventPlace place)
        {
            string target = Path.Combine(this.pathToJsonFiles, FileNameFor(place));

            if (!Scoring.JsonStore.TryWriteAtomic(target, place, out var error))
            {
                LogWarn(string.Format(
                    "SavePlace: failed to write place id={0} name={1}: {2}", place.Id, place.Name, error));
                return false;
            }

            // Stale files from a rename are removed only after the write succeeded.
            try
            {
                foreach (var file in Directory.GetFiles(this.pathToJsonFiles, "*_" + place.Id + ".json"))
                {
                    if (!string.Equals(file, target, System.StringComparison.OrdinalIgnoreCase))
                        File.Delete(file);
                }
            }
            catch (System.Exception e)
            {
                LogWarn(string.Format(
                    "SavePlace: saved place id={0} but could not remove its stale file(s): {1}", place.Id, e.Message));
            }
            return true;
        }

        // "Name_Id.json"; the id keeps it unique, the name is just sanitized.
        public static string FileNameFor(EventPlace place)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder();
            foreach (char c in place.Name ?? "")
                sb.Append(Array.IndexOf(invalid, c) >= 0 || char.IsWhiteSpace(c) ? '_' : c);
            string name = sb.ToString().Trim('.', '_');
            if (name.Length > 48) name = name.Substring(0, 48);
            if (name.Length == 0) name = "place";
            return name + "_" + place.Id + ".json";
        }

        public bool AddPlace(EventPlace place, bool skipJsonExport = false)
        {
            // Check first: re-adding the same object would bump its Id and desync it from its file.
            if (this.EventPlaces.Contains(place)) return false;

            place.Id = this.LastId++;
            SaveIdCounter();
            if (this.EventPlaces.Add(place))
            {
                if (!skipJsonExport) SavePlace(place);
                return true;
            }
            return false;
        }
    }
}
