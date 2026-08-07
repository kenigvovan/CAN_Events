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

        // Null-tolerant so the manager stays constructible without an API (tests, tooling).
        void Log(string message) => logger?.Notification("[canevents] " + message);
        void LogWarn(string message) => logger?.Warning("[canevents] " + message);
        public void LoadEventPlaces()
        {
            if (Directory.Exists(pathToJsonFiles))
            {
                foreach (var eventPlaceFile in Directory.GetFiles(pathToJsonFiles, "*.json"))
                {
                    // Per-file isolation: one unreadable place must not cost the rest of the
                    // arena list, which is hand-made work that cannot be regenerated.
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

                    // HasBounds always survives a round-trip, the corner objects may not. A flag
                    // without corners makes consumers draw/match a footprint that isn't there.
                    if (eventPlace.HasBounds && (eventPlace.BoundsMin == null || eventPlace.BoundsMax == null))
                    {
                        LogWarn(string.Format(
                            "LoadEventPlaces: {0}: HasBounds was true but a corner is null - clearing the flag.",
                            Path.GetFileName(eventPlaceFile)));
                        eventPlace.HasBounds = false;
                    }

                    this.EventPlaces.Add(eventPlace);
                    if (eventPlace.Id >= LastId)
                    {
                        LastId = eventPlace.Id + 1;
                    }
                    int bdCount = eventPlace.BehaviorData?.Count ?? -1;
                    bool hasParkour = eventPlace.BehaviorData?.ContainsKey("Parkour") ?? false;
                    // Bounds are logged because the flag and the corner objects can disagree after a load.
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
        }
        private static readonly Random _random = new Random();

        /// <summary>
        /// A free arena that can host these place tags, picked at random among suitable ones.
        /// <paramref name="allowedIds"/> narrows the pool to a whitelist (e.g. a tournament pinned to
        /// specific arenas); null or empty means the whole server.
        /// </summary>
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

        /// <summary>The place with this id, or null.</summary>
        public EventPlace FindById(long id) => EventPlaces.FirstOrDefault(it => it.Id == id);

        public bool DeletePlace(long id)
        {
            EventPlace found = FindById(id);
            if (found != null)
            {
                this.EventPlaces.RemoveWhere(it => it.Id == id);

                try
                {
                    var filePath = Path.Combine(this.pathToJsonFiles, found.Name + "_" + found.Id + ".json");
                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                    }
                    // Also match without the name, in case the file was renamed
                    foreach (var file in Directory.GetFiles(this.pathToJsonFiles, "*_" + found.Id + ".json"))
                    {
                        File.Delete(file);
                    }
                }
                catch (Exception e)
                {
                    // The place is gone from memory but its file is not, so it comes back on restart.
                    LogWarn("deleted place " + found.Name + " (#" + found.Id
                        + ") but could not remove its file: " + e.Message);
                }

                return true;
            }
            return false;
        }
        /// <summary>
        /// Writes a place to disk via temp-file-then-swap, so a failure at any step leaves the previous
        /// file intact; a place is hand-made work that cannot be regenerated.
        /// </summary>
        public void SavePlace(EventPlace place)
        {
            string target = Path.Combine(this.pathToJsonFiles, place.Name + "_" + place.Id + ".json");

            if (!Scoring.JsonStore.TryWriteAtomic(target, place, out var error))
            {
                LogWarn(string.Format(
                    "SavePlace: failed to write place id={0} name={1}: {2}", place.Id, place.Name, error));
                return;
            }

            // Only now drop stale files for this id (a rename orphans one). Deleting before the swap
            // succeeded would mean a failed write costs the place entirely.
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
                // The place is saved; a leftover file under the old name is cosmetic.
                LogWarn(string.Format(
                    "SavePlace: saved place id={0} but could not remove its stale file(s): {1}", place.Id, e.Message));
            }
        }

        public bool AddPlace(EventPlace place, bool skipJsonExport = false)
        {
            // Guard before assigning an Id: the set is reference-keyed, so re-adding the same object
            // would bump its Id while Add returns false, desyncing it from the file already written.
            if (this.EventPlaces.Contains(place)) return false;

            place.Id = this.LastId++;
            if (this.EventPlaces.Add(place))
            {
                if (!skipJsonExport) SavePlace(place);
                return true;
            }
            return false;
        }
    }
}
