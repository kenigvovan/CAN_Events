using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Vintagestory.API.Config;

namespace canevents.src.EventManagers.events
{
    // Match modifiers: "noarmor", "health:0.5", "loadout:Archer".
    // New one: spec below + "canevents:mutator-<key>" lang key + the effect where it belongs.
    public static class EventMutators
    {
        // Strips wearables from the kit; outfit clothes still apply.
        public const string NoArmor = "noarmor";

        public const string NoShields = "noshields";

        // Drops the NoFriendlyFire behavior from the match.
        public const string FriendlyFire = "friendlyfire";

        // Value "shared" rolls one set for the whole match.
        public const string RandomLoadout = "randomloadout";

        // "loadout:Archer" forces that set; refused if the event has no such set.
        public const string Loadout = "loadout";

        // Start/respawn health fraction; max health untouched, so nothing to clean up after a crash.
        public const string Health = "health";

        // Non-persistent stat, so a crash or relog drops it.
        public const string Speed = "speed";

        public enum ValueKind
        {
            None,
            // Required, within [Min, Max].
            Number,
            Text,
            OptionalKeyword
        }

        public sealed class MutatorSpec
        {
            public string Key;
            public ValueKind Kind;
            public float Min, Max;
            public string[] Keywords = Array.Empty<string>();
        }

        public static readonly MutatorSpec[] Known =
        {
            new MutatorSpec { Key = NoArmor,       Kind = ValueKind.None },
            new MutatorSpec { Key = NoShields,     Kind = ValueKind.None },
            new MutatorSpec { Key = FriendlyFire,  Kind = ValueKind.None },
            new MutatorSpec { Key = RandomLoadout, Kind = ValueKind.OptionalKeyword, Keywords = new[] { "shared" } },
            new MutatorSpec { Key = Loadout,       Kind = ValueKind.Text },
            new MutatorSpec { Key = Health,        Kind = ValueKind.Number, Min = 0.1f, Max = 1f },
            new MutatorSpec { Key = Speed,         Kind = ValueKind.Number, Min = 0.5f, Max = 2f },
        };

        public static MutatorSpec SpecOf(string key)
            => Known.FirstOrDefault(s => s.Key.Equals(key?.Trim(), StringComparison.OrdinalIgnoreCase));

        // Unknown keys are dropped silently.
        public static IEnumerable<string> Normalize(IEnumerable<string> keys)
            => (keys ?? Array.Empty<string>()).Select(k => SpecOf(k)?.Key).Where(k => k != null);

        // Numbers are parsed culture-invariant.
        public static bool TryParse(string raw, out string key, out string value, out string error)
        {
            key = null; value = null; error = raw;
            if (string.IsNullOrWhiteSpace(raw)) return false;

            int colon = raw.IndexOf(':');
            string k = (colon < 0 ? raw : raw.Substring(0, colon)).Trim().ToLowerInvariant();
            string v = colon < 0 ? null : raw.Substring(colon + 1).Trim();
            if (v != null && v.Length == 0) v = null;

            var spec = SpecOf(k);
            if (spec == null) return false;

            switch (spec.Kind)
            {
                case ValueKind.None:
                    if (v != null) return false;
                    break;

                case ValueKind.Number:
                    if (v == null || !float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float num)
                        || num < spec.Min || num > spec.Max) return false;
                    v = num.ToString(CultureInfo.InvariantCulture);
                    break;

                case ValueKind.Text:
                    if (v == null) return false;
                    break;

                case ValueKind.OptionalKeyword:
                    if (v != null && !spec.Keywords.Contains(v, StringComparer.OrdinalIgnoreCase)) return false;
                    v = v?.ToLowerInvariant();
                    break;
            }

            key = spec.Key;
            value = v;
            error = null;
            return true;
        }

        public static float FloatOf(IReadOnlyDictionary<string, string> mutators, string key, float fallback)
        {
            if (mutators == null || !mutators.TryGetValue(key, out var v) || v == null) return fallback;
            return float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float num) ? num : fallback;
        }

        public static string NameOf(string key, string value)
        {
            switch (key)
            {
                case Health:
                    float frac = float.TryParse(value ?? "", NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : 1f;
                    return Lang.Get("canevents:mutator-health", (int)Math.Round(frac * 100));
                case Speed:
                    return Lang.Get("canevents:mutator-speed", value ?? "1");
                case Loadout:
                    return Lang.Get("canevents:mutator-loadout", value ?? "?");
                default:
                    // Unknown key (client/server version mismatch): show it raw.
                    return SpecOf(key) != null ? Lang.Get("canevents:mutator-" + key) : key;
            }
        }

        public static string Describe(IReadOnlyDictionary<string, string> mutators)
        {
            if (mutators == null || mutators.Count == 0) return null;
            return string.Join(", ", mutators.Select(kv => NameOf(kv.Key, kv.Value)));
        }

        // From the packed "key" / "key:value" form.
        public static string Describe(IEnumerable<string> packed)
        {
            var dict = ParseAll(packed, out _);
            return dict.Count == 0 ? null : Describe(dict);
        }

        public static Dictionary<string, string> ParseAll(IEnumerable<string> raw, out List<string> rejected)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            rejected = new List<string>();
            if (raw == null) return result;

            foreach (var entry in raw)
            {
                if (TryParse(entry, out var key, out var value, out _)) result[key] = value;
                else if (!string.IsNullOrWhiteSpace(entry)) rejected.Add(entry);
            }
            return result;
        }

        public static string[] Pack(IReadOnlyDictionary<string, string> mutators)
            => mutators == null || mutators.Count == 0
                ? Array.Empty<string>()
                : mutators.Select(kv => kv.Value == null ? kv.Key : kv.Key + ":" + kv.Value).ToArray();
    }
}
