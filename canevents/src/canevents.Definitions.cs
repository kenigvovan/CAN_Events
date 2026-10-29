using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.commands;
using canevents.src.EventManagers;
using canevents.src.EventManagers.eventBehavior;
using canevents.src.EventManagers.eventBehavior.abstracts;
using canevents.src.EventManagers.events;
using canevents.src.EventPlacesManagers;
using canevents.src.EventPlacesManagers.Places;
using canevents.src.HarmonyPatches;
using canparty;
using canevents.src.QueueManagers;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.API.Util;

namespace canevents.src
{
    public partial class canevents
    {
        // Shared by startup and /eventadmin reload. problems is null at startup.
        private static void Problem(ICoreAPI api, List<string> problems, EnumLogType level, string format, params object[] args)
        {
            api.World.Logger.Log(level, format, args);
            problems?.Add(string.Format(format, args));
        }

        // Duplicate codes are skipped: they would share a queue manager.
        private List<EventDefinitionFile> LoadEventDefinitions(ICoreAPI api, List<string> problems, List<string> failedFiles)
        {
            var result = new List<EventDefinitionFile>();

            // Assets only seed ModConfig; existing files there are never overwritten.
            var userEventsDir = ModPaths.Events;
            try
            {
                if (!System.IO.Directory.Exists(userEventsDir)) System.IO.Directory.CreateDirectory(userEventsDir);
                foreach (var asset in api.Assets.GetMany("config/events/", "canevents"))
                {
                    if (!asset.Location.Path.EndsWithOrdinal(".json")) continue;
                    var fname = System.IO.Path.GetFileName(asset.Location.Path);
                    var target = System.IO.Path.Combine(userEventsDir, fname);
                    if (System.IO.File.Exists(target)) continue;
                    System.IO.File.WriteAllBytes(target, asset.Data);
                    api.World.Logger.Notification("Seeded event definition: {0}", fname);
                }
            }
            catch (Exception exSeedEvents)
            {
                Problem(api, problems, EnumLogType.Warning, "Failed to seed default event definitions: {0}", exSeedEvents.Message);
            }

            if (!System.IO.Directory.Exists(userEventsDir)) return result;

            var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in System.IO.Directory.GetFiles(userEventsDir, "*.json"))
            {
                var fname = System.IO.Path.GetFileName(file);
                try
                {
                    var text = System.IO.File.ReadAllText(file);
                    var eventType = Newtonsoft.Json.JsonConvert.DeserializeObject<CANEventType>(text);
                    if (eventType == null || string.IsNullOrEmpty(eventType.Code))
                    {
                        Problem(api, problems, EnumLogType.Warning, "Event definition {0} has no code, skipped.", fname);
                        failedFiles?.Add(fname);
                        continue;
                    }
                    if (!seenCodes.Add(eventType.Code))
                    {
                        Problem(api, problems, EnumLogType.Warning,
                            "Event definition {0}: code '{1}' is already used by another file, skipped.", fname, eventType.Code);
                        continue;
                    }
                    eventType.InitType();
                    result.Add(new EventDefinitionFile { FileName = fname, Type = eventType, Json = JToken.Parse(text) });
                }
                catch (Exception e)
                {
                    Problem(api, problems, EnumLogType.Error, "Event definition {0} could not be loaded: {1}", fname, e.Message);
                    failedFiles?.Add(fname);
                }
            }
            return result;
        }

        // Null if the event can't be built; a bad behavior or mutator only drops that entry.
        private CANEvent BuildEvent(ICoreAPI api, CANEventType it, List<string> problems)
        {
            try
            {
                // EventType is just a label, behaviors define the event.
                var tmpEvent = new CANEvent();
                foreach (var behaior in it.Behaviors ?? new List<CANEventBehavioreType>())
                {
                    if (behaior.Name != null && CANEventBehaviors.TryGetValue(behaior.Name, out var behaviorType))
                    {
                        var behaviorInstance = Activator.CreateInstance(behaviorType, new object[] { behaior.Name, behaior.Attributes, null }) as CANEventBehavior;
                        behaviorInstance.Initialize();
                        tmpEvent.Behaviors.Add(behaviorInstance);
                    }
                    else
                    {
                        Problem(api, problems, EnumLogType.Warning, "Event '{0}': unknown behavior '{1}' ignored. Known: {2}.",
                            it.Code, behaior.Name ?? "(no name)", string.Join(", ", CANEventBehaviors.Keys));
                    }
                }
                foreach (var en in it.EventPlaceTypes ?? Array.Empty<string>())
                {
                    var tag = PlaceTags.Normalize(en);
                    if (!string.IsNullOrEmpty(tag)) tmpEvent.EventTypes.Add(tag);
                }
                tmpEvent.Code = it.Code;
                tmpEvent.Category = string.IsNullOrWhiteSpace(it.Category) ? it.EventType : it.Category;
                tmpEvent.Reward = it.Reward;
                tmpEvent.EntryFee = it.EntryFee;
                tmpEvent.Rated = it.Rated;
                tmpEvent.CountdownSeconds = it.CountdownSeconds;
                tmpEvent.Mutators = EventMutators.ParseAll(it.Mutators, out var badMutators);
                foreach (var bad in badMutators)
                    Problem(api, problems, EnumLogType.Warning, "Event '{0}': unknown or malformed mutator '{1}' ignored.", it.Code, bad);
                foreach (var disabled in EventMutators.Normalize(config.disabledMutators))
                    if (tmpEvent.Mutators.Remove(disabled))
                        api.World.Logger.Notification(
                            "Event '{0}': mutator '{1}' removed - disabled in config.", it.Code, disabled);
                return tmpEvent;
            }
            catch (Exception e)
            {
                // Bad behavior attributes throw from ctor/Initialize; skip just this event.
                Problem(api, problems, EnumLogType.Error, "Event '{0}' could not be built: {1}",
                    it.Code, (e.InnerException ?? e).Message);
                return null;
            }
        }

        private static void SeedDefaultSchematics(ICoreAPI api)
        {
            try
            {
                var schematicsDir = ModPaths.Schematics;
                if (!System.IO.Directory.Exists(schematicsDir)) System.IO.Directory.CreateDirectory(schematicsDir);
                foreach (var asset in api.Assets.GetMany("config/defaultschematics/", "canevents"))
                {
                    if (!asset.Location.Path.EndsWithOrdinal(".json")) continue;
                    var fname = System.IO.Path.GetFileName(asset.Location.Path);
                    var target = System.IO.Path.Combine(schematicsDir, fname);
                    if (System.IO.File.Exists(target)) continue;
                    System.IO.File.WriteAllBytes(target, asset.Data);
                    api.World.Logger.Notification("Seeded default schematic: {0}", fname);
                }
            }
            catch (Exception exSeed)
            {
                api.World.Logger.Warning("Failed to seed default schematics: {0}", exSeed.Message);
            }
        }

        // Shipped assets first, then ModConfig overrides by code.
        private static List<ArenaTemplate> LoadArenaTemplates(ICoreAPI api, List<string> problems, List<string> failedFiles)
        {
            var templates = new List<ArenaTemplate>();

            foreach (KeyValuePair<AssetLocation, JObject> entry in api.Assets.GetMany<JObject>(api.World.Logger, "config/arenas/", "canevents"))
            {
                if (entry.Key.Path.EndsWithOrdinal(".json"))
                {
                    try
                    {
                        var template = entry.Value.ToObject<ArenaTemplate>();
                        templates.Add(template);
                        api.World.Logger.Notification("Loaded arena template: {0}", template.Code);
                    }
                    catch (Exception e)
                    {
                        Problem(api, problems, EnumLogType.Error, "Arena template {0} could not be loaded: {1}", entry.Key, e.Message);
                    }
                }
            }

            var userArenasDir = ModPaths.Arenas;
            if (!System.IO.Directory.Exists(userArenasDir))
            {
                System.IO.Directory.CreateDirectory(userArenasDir);
            }
            foreach (var file in System.IO.Directory.EnumerateFiles(userArenasDir, "*.json"))
            {
                var fname = System.IO.Path.GetFileName(file);
                try
                {
                    var json = System.IO.File.ReadAllText(file);
                    var template = Newtonsoft.Json.JsonConvert.DeserializeObject<ArenaTemplate>(json);
                    if (template == null || string.IsNullOrEmpty(template.Code))
                    {
                        Problem(api, problems, EnumLogType.Warning, "Arena template {0} has no code, skipped.", fname);
                        failedFiles?.Add(fname);
                        continue;
                    }
                    // Case-insensitive, like all template lookups.
                    var existing = templates.FindIndex(t =>
                        string.Equals(t.Code, template.Code, StringComparison.OrdinalIgnoreCase));
                    if (existing >= 0)
                    {
                        templates[existing] = template;
                        api.World.Logger.Notification("Overrode arena template from ModConfig: {0}", template.Code);
                    }
                    else
                    {
                        templates.Add(template);
                        api.World.Logger.Notification("Loaded arena template from ModConfig: {0}", template.Code);
                    }
                }
                catch (Exception e)
                {
                    Problem(api, problems, EnumLogType.Error, "Arena template {0} could not be loaded: {1}", fname, e.Message);
                    failedFiles?.Add(fname);
                }
            }
            return templates;
        }

        public sealed class DefinitionReloadReport
        {
            // Non-empty means nothing was applied: these events still have players queued.
            public List<string> BlockedBy = new List<string>();
            public List<string> Added = new List<string>();
            public List<string> Changed = new List<string>();
            public List<string> Removed = new List<string>();
            // File failed to load; the previously loaded version stays.
            public List<string> KeptOnError = new List<string>();
            public List<string> DroppedQueues = new List<string>();
            public int Unchanged;
            public int ActiveEvents;
            public int Templates;
            public List<string> KeptTemplates = new List<string>();
            public List<string> Problems = new List<string>();
            public int RunningMatches;
        }

        // Live matches keep their old CANEvent clones. Unchanged events keep the same object.
        // Changed/removed events with players queued block the reload unless dropQueues.
        public DefinitionReloadReport ReloadDefinitions(ICoreServerAPI api, bool dropQueues)
        {
            var report = new DefinitionReloadReport();
            var manager = CANEventManager;

            var failedFiles = new List<string>();
            var defs = LoadEventDefinitions(api, report.Problems, failedFiles);

            var next = new List<CANEvent>();
            var nextJson = new Dictionary<string, JToken>(StringComparer.OrdinalIgnoreCase);
            foreach (var def in defs)
            {
                string code = def.Type.Code;
                if (!def.Type.Enabled)
                {
                    api.World.Logger.Notification("Event '{0}' is disabled in config, skipping.", code);
                    continue;
                }

                var current = manager.FindEventByCode(code);
                // Case-only rename counts as a change.
                bool unchanged = current != null && current.Code == code
                    && eventJsonByCode.TryGetValue(code, out var was) && JToken.DeepEquals(was, def.Json);
                if (unchanged)
                {
                    next.Add(current);
                    nextJson[code] = def.Json;
                    report.Unchanged++;
                    continue;
                }

                var built = BuildEvent(api, def.Type, report.Problems);
                if (built == null)
                {
                    // Keep the loaded version.
                    if (current != null && eventJsonByCode.TryGetValue(current.Code, out var oldJson))
                    {
                        next.Add(current);
                        nextJson[current.Code] = oldJson;
                        report.KeptOnError.Add(current.Code);
                    }
                    continue;
                }
                next.Add(built);
                nextJson[code] = def.Json;
                (current == null ? report.Added : report.Changed).Add(code);
            }

            // Unreadable file: keep the event loaded from it.
            var nextCodes = new HashSet<string>(next.Select(e => e.Code), StringComparer.OrdinalIgnoreCase);
            foreach (var fname in failedFiles)
            {
                if (!eventCodeByFile.TryGetValue(fname, out var oldCode) || nextCodes.Contains(oldCode)) continue;
                var current = manager.FindEventByCode(oldCode);
                if (current == null || !eventJsonByCode.TryGetValue(current.Code, out var oldJson)) continue;
                next.Add(current);
                nextJson[current.Code] = oldJson;
                nextCodes.Add(current.Code);
                report.KeptOnError.Add(current.Code);
            }

            report.Removed = manager.Events
                .Where(e => !nextCodes.Contains(e.Code))
                .Select(e => e.Code)
                .ToList();

            report.BlockedBy = manager.ReplaceEventDefinitions(next, dropQueues, out report.DroppedQueues);
            if (report.BlockedBy.Count > 0) return report;

            eventDefinitions = defs;
            eventJsonByCode = nextJson;
            var codeByFile = defs.ToDictionary(d => d.FileName, d => d.Type.Code, StringComparer.Ordinal);
            // Failed files keep their old mapping for the next reload.
            foreach (var fname in failedFiles)
                if (eventCodeByFile.TryGetValue(fname, out var oldCode) && nextCodes.Contains(oldCode))
                    codeByFile[fname] = oldCode;
            eventCodeByFile = codeByFile;
            report.ActiveEvents = next.Count;

            ReloadArenaTemplates(api, report);
            report.RunningMatches = manager.RunningEventCount;
            return report;
        }

        // Templates used by on-demand arenas or with unreadable files are kept.
        private void ReloadArenaTemplates(ICoreServerAPI api, DefinitionReloadReport report)
        {
            var failedFiles = new List<string>();
            var fresh = LoadArenaTemplates(api, report.Problems, failedFiles);

            bool Has(string code) => fresh.Any(t => string.Equals(t.Code, code, StringComparison.OrdinalIgnoreCase));
            ArenaTemplate Old(string code) => arenaTemplates.Find(t => string.Equals(t.Code, code, StringComparison.OrdinalIgnoreCase));

            // File name == template code (ModPaths.ArenaFile).
            foreach (var fname in failedFiles)
            {
                string code = System.IO.Path.GetFileNameWithoutExtension(fname);
                var old = Old(code);
                if (old == null || Has(code)) continue;
                fresh.Add(old);
                report.KeptTemplates.Add(old.Code + " (" + fname + " unreadable)");
            }

            var onDemand = CANEventManager.eventPlacesManager.EventPlaces
                .Where(p => p.SchematicOnDemand && !string.IsNullOrEmpty(p.SchematicTemplateCode))
                .GroupBy(p => p.SchematicTemplateCode, StringComparer.OrdinalIgnoreCase);
            foreach (var group in onDemand)
            {
                if (Has(group.Key)) continue;
                string places = string.Join(", ", group.Select(p => p.Name ?? ("#" + p.Id)));
                var old = Old(group.Key);
                if (old != null)
                {
                    fresh.Add(old);
                    report.KeptTemplates.Add(old.Code + " (on-demand: " + places + ")");
                }
                else
                {
                    Problem(api, report.Problems, EnumLogType.Warning,
                        "On-demand arena(s) {0} reference template '{1}', which is not loaded.", places, group.Key);
                }
            }

            arenaTemplates.Clear();
            arenaTemplates.AddRange(fresh);
            report.Templates = arenaTemplates.Count;
        }
    }
}
