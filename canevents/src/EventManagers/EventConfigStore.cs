using System;
using System.IO;
using canevents.src.Scoring;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Config;

namespace canevents.src.EventManagers
{
    /// <summary>
    /// Edits the event JSON files under ModConfig/canevents/events in place: find the file whose
    /// "code" matches (case-insensitively), parse it, apply the caller's change, write it back
    /// atomically. An unparseable neighbour is skipped rather than aborting the edit.
    /// </summary>
    public class EventConfigStore
    {
        private readonly string directory;

        /// <param name="directory">Defaults to the mod's own event config folder.</param>
        public EventConfigStore(string directory = null)
        {
            this.directory = directory ?? Path.Combine(GamePaths.ModConfig, "canevents", "events");
        }

        /// <summary>
        /// Applies <paramref name="edit"/> to the event file with this code and saves it.
        /// <paramref name="edit"/> returns null when it succeeded, or a message describing why the
        /// file can't take the change - in which case nothing is written.
        /// </summary>
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

                    var fileCode = obj["code"]?.Value<string>();
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
    }
}
