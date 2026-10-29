using System;
using System.IO;
using canevents.src.Scoring;
using Newtonsoft.Json.Linq;

namespace canevents.src.EventManagers
{
    // In-place edits of the event JSON files under ModConfig/canevents/events, written back atomically.
    public class EventConfigStore
    {
        private readonly string directory;

        public EventConfigStore(string directory = null)
        {
            this.directory = directory ?? ModPaths.Events;
        }

        // edit returns null on success, else the reason; nothing is written then.
        public bool Edit(string code, Func<JObject, string> edit, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(code)) { error = "no event code"; return false; }

            try
            {
                if (!Directory.Exists(directory)) { error = "events folder missing"; return false; }

                foreach (var file in Directory.GetFiles(directory, "*.json"))
                {
                    JObject obj;
                    try { obj = JObject.Parse(File.ReadAllText(file)); }
                    catch { continue; } // skip an unparseable file rather than abort the whole edit

                    // Any casing of the key: the loader reads "Code" as happily as "code".
                    var fileCode = obj.GetValue("code", StringComparison.OrdinalIgnoreCase)?.Value<string>();
                    if (string.IsNullOrEmpty(fileCode)
                        || !fileCode.Equals(code, StringComparison.OrdinalIgnoreCase)) continue;

                    string reason = edit(obj);
                    if (reason != null) { error = reason; return false; }

                    return JsonStore.TryWriteAtomic(file, obj, out error);
                }

                error = "no JSON file with code '" + code + "'";
                return false;
            }
            catch (Exception e) { error = e.Message; return false; }
        }

        // The behavior's "attributes" object is created when missing; every other key is kept.
        public bool EditBehaviorAttributes(string code, string behaviorName, Action<JObject> edit, out string error)
            => Edit(code, obj =>
            {
                var behs = (obj["behaviors"] ?? obj["Behaviors"]) as JArray;
                if (behs == null) return "no behaviors array";

                JObject beh = null;
                foreach (var b in behs)
                {
                    if (b is not JObject bo) continue;
                    var name = (bo["name"] ?? bo["Name"])?.Value<string>();
                    if (string.Equals(name, behaviorName, StringComparison.OrdinalIgnoreCase)) { beh = bo; break; }
                }
                if (beh == null) return "no " + behaviorName + " behavior";

                string key = beh["Attributes"] != null && beh["attributes"] == null ? "Attributes" : "attributes";
                if (beh[key] is not JObject attr) { attr = new JObject(); beh[key] = attr; }
                edit(attr);
                return null;
            }, out error);
    }
}
