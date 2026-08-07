using System.Collections.Generic;
using System.Linq;

namespace canevents.src.Network
{
    /// <summary>
    /// House format for a variable-length list on the wire: one string, pieces separated by ';'.
    /// Used where a per-row list (a match's slots, a tournament's event codes) doesn't fit the
    /// parallel flat arrays the messages otherwise use. Item sets keep their own packer
    /// (<see cref="EventManagers.events.ItemSetUtil"/>), which also carries a quantity per entry.
    /// </summary>
    public static class PackedList
    {
        public const char Separator = ';';

        /// <summary>Packs a list; nulls become empty pieces, so slot positions are preserved.</summary>
        public static string Pack(IEnumerable<string> values)
            => values == null ? "" : string.Join(Separator.ToString(), values.Select(v => v ?? ""));

        /// <summary>
        /// Unpacks into the pieces as sent, empty entries included - an empty piece is a real value
        /// (an unfilled bracket slot), not padding to be dropped.
        /// </summary>
        public static List<string> Unpack(string packed)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(packed)) return result;
            foreach (var piece in packed.Split(Separator)) result.Add(piece);
            return result;
        }

        /// <summary>Unpacks and drops the empty pieces, for lists where position carries no meaning.</summary>
        public static List<string> UnpackNonEmpty(string packed)
            => Unpack(packed).Where(s => !string.IsNullOrEmpty(s)).ToList();
    }
}
