using System.Collections.Generic;
using System.Linq;

namespace canevents.src.Network
{
    // A per-row list on the wire as one ';'-separated string, where parallel flat arrays don't fit.
    // Item sets have their own packer with quantities (ItemSetUtil).
    public static class PackedList
    {
        public const char Separator = ';';

        // Nulls become empty pieces, so slot positions are preserved.
        public static string Pack(IEnumerable<string> values)
            => values == null ? "" : string.Join(Separator.ToString(), values.Select(v => v ?? ""));

        // Empty pieces kept: an empty piece is a real value (an unfilled bracket slot).
        public static List<string> Unpack(string packed)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(packed)) return result;
            foreach (var piece in packed.Split(Separator)) result.Add(piece);
            return result;
        }

        // For lists where position carries no meaning.
        public static List<string> UnpackNonEmpty(string packed)
            => Unpack(packed).Where(s => !string.IsNullOrEmpty(s)).ToList();
    }
}
