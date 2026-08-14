using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace canevents.src.EventPlacesManagers.Places
{
    /// <summary>
    /// Place-type tags, free-form strings at runtime (replacing the old closed <c>EventPlaceType</c>
    /// enum) so an unknown tag can't crash anything. <see cref="Known"/> is the canonical list for
    /// GUI checkboxes and command completion.
    /// </summary>
    public static class PlaceTags
    {
        public const string Arena1x1 = "ARENA1X1";
        public const string Arena2x2 = "ARENA2X2";
        public const string Arena3x3 = "ARENA3X3";
        public const string Arena5x5 = "ARENA5X5";
        public const string Ffa = "FFA";
        public const string Spleef = "SPLEEF";
        public const string Parkour = "PARKOUR";
        public const string Deathmatch = "DEATHMATCH";
        public const string Koth = "KOTH";
        public const string HungerGames = "HUNGERGAMES";
        public const string Ctf = "CTF";
        public const string MurderMystery = "MURDERMYSTERY";

        // Appended, never reordered: LegacyOrdinals below maps old numeric tags by position.
        public static readonly string[] Known =
        {
            Arena1x1, Arena2x2, Arena3x3, Arena5x5, Ffa,
            Spleef, Parkour, Deathmatch, Koth, HungerGames, Ctf, MurderMystery
        };

        public static string Normalize(string tag) => tag?.Trim().ToUpperInvariant();
    }

    /// <summary>
    /// Reads <see cref="EventPlace.placeTypes"/> tolerantly: legacy files store the old enum as INTEGER
    /// ordinals, new ones store tag strings. Always writes strings, upgrading the file on next save.
    /// </summary>
    public class PlaceTagsConverter : JsonConverter<string[]>
    {
        // Old enum order: index = the integer ordinal that legacy files contain.
        private static readonly string[] LegacyOrdinals =
        {
            "NONE", PlaceTags.Arena1x1, PlaceTags.Arena2x2, PlaceTags.Arena3x3, PlaceTags.Arena5x5,
            PlaceTags.Ffa, PlaceTags.Spleef, PlaceTags.Parkour, PlaceTags.Deathmatch,
            PlaceTags.Koth, PlaceTags.HungerGames
        };

        public override string[] ReadJson(JsonReader reader, Type objectType, string[] existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null) return null;
            var arr = JArray.Load(reader);
            var list = new List<string>();
            foreach (var tok in arr)
            {
                if (tok.Type == JTokenType.Integer)
                {
                    int i = (int)tok;
                    if (i > 0 && i < LegacyOrdinals.Length) list.Add(LegacyOrdinals[i]);
                }
                else
                {
                    var s = PlaceTags.Normalize(tok.ToString());
                    if (!string.IsNullOrEmpty(s)) list.Add(s);
                }
            }
            return list.ToArray();
        }

        public override void WriteJson(JsonWriter writer, string[] value, JsonSerializer serializer)
        {
            writer.WriteStartArray();
            if (value != null)
                foreach (var v in value) writer.WriteValue(v);
            writer.WriteEndArray();
        }
    }
}
