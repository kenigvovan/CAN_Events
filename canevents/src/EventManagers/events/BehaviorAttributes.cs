using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace canevents.src.EventManagers.events
{
    public enum SettingType { Int, Float, Bool, String }

    // A scalar key a behavior read, with the fallback it would use.
    public sealed class KnownSetting
    {
        public string Key;
        public SettingType Type;
        public object Fallback;
    }

    // Behavior config reader that tracks read keys, so WarnUnknown can flag typos. The typed reads
    // also make the list of a behavior's settings, which the admin editor offers.
    public class BehaviorAttributes
    {
        private readonly JsonObject attributes;
        private readonly ILogger logger;
        private readonly string owner;
        private readonly HashSet<string> read = new HashSet<string>();
        private readonly List<KnownSetting> known = new List<KnownSetting>();
        private readonly HashSet<string> knownKeys = new HashSet<string>();

        // In read order; a key read twice keeps its first type.
        public IReadOnlyList<KnownSetting> Known => known;

        // RegisterGameTickListener is retired (see WantsTick) but old configs still have it.
        private static readonly HashSet<string> AlwaysExpected =
            new HashSet<string> { "RegisterGameTickListener", "comment" };

        public BehaviorAttributes(JsonObject attributes, ILogger logger, string owner)
        {
            this.attributes = attributes;
            this.logger = logger;
            this.owner = owner;
        }

        public float Float(string key, float fallback) => Read(key, SettingType.Float, fallback, t => t.AsFloat(fallback));
        public int Int(string key, int fallback) => Read(key, SettingType.Int, fallback, t => t.AsInt(fallback));
        public bool Bool(string key, bool fallback) => Read(key, SettingType.Bool, fallback, t => t.AsBool(fallback));
        public string String(string key, string fallback = null) => Read(key, SettingType.String, fallback, t => t.AsString(fallback));

        public JsonObject Raw(string key)
        {
            read.Add(key);
            return attributes?[key];
        }

        public bool Has(string key)
        {
            read.Add(key);
            return attributes?[key]?.Exists == true;
        }

        private T Read<T>(string key, SettingType type, T fallback, System.Func<JsonObject, T> convert)
        {
            read.Add(key);
            if (knownKeys.Add(key)) known.Add(new KnownSetting { Key = key, Type = type, Fallback = fallback });
            var node = attributes?[key];
            if (node?.Exists != true)
            {
                logger?.VerboseDebug("[canevents] {0}: '{1}' not configured, using {2}", owner, key, fallback);
                return fallback;
            }
            return convert(node);
        }

        // Call once after the behavior has initialized.
        public void WarnUnknown()
        {
            if (!(attributes?.Token is Newtonsoft.Json.Linq.JObject obj)) return;

            foreach (var property in obj.Properties())
            {
                if (read.Contains(property.Name)) continue;
                if (AlwaysExpected.Contains(property.Name)) continue;
                logger?.Warning("[canevents] {0}: attribute '{1}' is set in the config but nothing "
                    + "reads it - check the spelling.", owner, property.Name);
            }
        }
    }
}
