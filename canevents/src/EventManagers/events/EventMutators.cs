using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Vintagestory.API.Config;

namespace canevents.src.EventManagers.events
{
    /// <summary>
    /// Match modifiers an event JSON (or a duel challenge) may switch on: "noarmor", "health:0.5",
    /// "loadout:Archer". A typed registry rather than loose strings so a typo in a config or a packet
    /// is caught and named at the border, instead of silently doing nothing mid-match.
    ///
    /// Adding one: a spec below + a "canevents:mutator-&lt;key&gt;" lang key + the actual effect at
    /// whichever point of the match pipeline it belongs to (LoadoutService for kit changes,
    /// KillScoringEB for stats, RequestLoadoutPicks for loadout choice, BuildDuelEvent for behaviors).
    /// </summary>
    public static class EventMutators
    {
        /// <summary>Wearables are stripped from the issued kit. Outfit clothes still apply.</summary>
        public const string NoArmor = "noarmor";

        /// <summary>Shields are stripped from the issued kit.</summary>
        public const string NoShields = "noshields";

        /// <summary>Team-mates can hurt each other: the NoFriendlyFire behavior is dropped from the
        /// match. Only meaningful on events that carry that behavior in the first place.</summary>
        public const string FriendlyFire = "friendlyfire";

        /// <summary>The server rolls everyone's loadout set; nobody is asked to pick. The optional
        /// value "shared" rolls once for the whole match instead of per player.</summary>
        public const string RandomLoadout = "randomloadout";

        /// <summary>Everyone plays the named loadout set ("loadout:Archer") - the "bows only" knob.
        /// Refused when the event has no set of that name.</summary>
        public const string Loadout = "loadout";

        /// <summary>Fraction of max health everyone starts (and respawns) at: "health:0.5". Start
        /// health only - max health is untouched, so there is nothing to clean up after a crash.</summary>
        public const string Health = "health";

        /// <summary>Walk-speed multiplier for the match: "speed:1.5". Applied as a non-persistent
        /// entity stat, so a crash or relog sheds it by itself.</summary>
        public const string Speed = "speed";

        public enum ValueKind
        {
            /// <summary>A bare flag; a value is rejected.</summary>
            None,
            /// <summary>A float within [Min, Max]; required.</summary>
            Number,
            /// <summary>Free text; required. Validated further by the feature (loadout set name).</summary>
            Text,
            /// <summary>Optional keyword from <see cref="MutatorSpec.Keywords"/>.</summary>
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

        /// <summary>Canonical keys of the entries naming a known mutator; anything else (typo in the
        /// config's disabledMutators) is dropped silently - disabling nothing is the safe reading.</summary>
        public static IEnumerable<string> Normalize(IEnumerable<string> keys)
            => (keys ?? Array.Empty<string>()).Select(k => SpecOf(k)?.Key).Where(k => k != null);

        /// <summary>
        /// Parses one "key" / "key:value" string into its canonical pair. False (with the offending
        /// part in <paramref name="error"/>) on an unknown key, a missing/forbidden value, or a number
        /// out of range. Culture-invariant: "0.5" everywhere, whatever the server locale.
        /// </summary>
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

        /// <summary>Reads a parsed value back as a float; the fallback covers "not set".</summary>
        public static float FloatOf(IReadOnlyDictionary<string, string> mutators, string key, float fallback)
        {
            if (mutators == null || !mutators.TryGetValue(key, out var v) || v == null) return fallback;
            return float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float num) ? num : fallback;
        }

        /// <summary>Localized display of one mutator, value included: "start health 50%".</summary>
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
                    // Unknown keys (older client vs newer server) still show as something readable.
                    return SpecOf(key) != null ? Lang.Get("canevents:mutator-" + key) : key;
            }
        }

        /// <summary>"no armor, start health 50%" - for prompts and cards. Null when empty.</summary>
        public static string Describe(IReadOnlyDictionary<string, string> mutators)
        {
            if (mutators == null || mutators.Count == 0) return null;
            return string.Join(", ", mutators.Select(kv => NameOf(kv.Key, kv.Value)));
        }

        /// <summary>Same, from the "key" / "key:value" wire form the packets carry.</summary>
        public static string Describe(IEnumerable<string> packed)
        {
            var dict = ParseAll(packed, out _);
            return dict.Count == 0 ? null : Describe(dict);
        }

        /// <summary>Parses a batch, collecting the strings that failed instead of throwing: a config
        /// with one typo should lose the typo, not the event.</summary>
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

        /// <summary>The wire form ("key" / "key:value") of a parsed set, for packets.</summary>
        public static string[] Pack(IReadOnlyDictionary<string, string> mutators)
            => mutators == null || mutators.Count == 0
                ? Array.Empty<string>()
                : mutators.Select(kv => kv.Value == null ? kv.Key : kv.Key + ":" + kv.Value).ToArray();
    }
}
