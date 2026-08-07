using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace canevents.src.EventManagers.events
{
    /// <summary>
    /// Reads a behavior's configuration and reports what it cannot find. A raw
    /// <c>Attributes["maxPartySize"].AsInt(2)</c> is silent about a misspelt key: the default comes
    /// back and the admin's setting does nothing. Here a fallback is logged at debug level, and
    /// <see cref="WarnUnknown"/> flags JSON keys nothing asked for - where a typo surfaces.
    /// </summary>
    public class BehaviorAttributes
    {
        private readonly JsonObject attributes;
        private readonly ILogger logger;
        private readonly string owner;
        private readonly HashSet<string> read = new HashSet<string>();

        /// <summary>Legitimate keys nothing reads through here: the base class takes
        /// RegisterGameTickListener in its constructor, and "comment" exists for people.</summary>
        private static readonly HashSet<string> AlwaysExpected =
            new HashSet<string> { "RegisterGameTickListener", "comment" };

        public BehaviorAttributes(JsonObject attributes, ILogger logger, string owner)
        {
            this.attributes = attributes;
            this.logger = logger;
            this.owner = owner;
        }

        public float Float(string key, float fallback) => Read(key, fallback, t => t.AsFloat(fallback));
        public int Int(string key, int fallback) => Read(key, fallback, t => t.AsInt(fallback));
        public bool Bool(string key, bool fallback) => Read(key, fallback, t => t.AsBool(fallback));
        public string String(string key, string fallback = null) => Read(key, fallback, t => t.AsString(fallback));

        /// <summary>The raw node, for the shapes that need their own parsing (tables, arrays).</summary>
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

        private T Read<T>(string key, T fallback, System.Func<JsonObject, T> convert)
        {
            read.Add(key);
            var node = attributes?[key];
            if (node?.Exists != true)
            {
                logger?.VerboseDebug("[canevents] {0}: '{1}' not configured, using {2}", owner, key, fallback);
                return fallback;
            }
            return convert(node);
        }

        /// <summary>Reports keys the JSON carries that nothing read - almost always a misspelling.
        /// Called once after a behavior has finished initializing.</summary>
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
