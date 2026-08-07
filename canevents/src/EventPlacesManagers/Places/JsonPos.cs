using Newtonsoft.Json.Linq;
using Vintagestory.API.MathTools;

namespace canevents.src.EventPlacesManagers.Places
{
    /// <summary>
    /// The one reading and writing of a block position stored in a place's behavior data
    /// (<c>{ "X": .., "Y": .., "Z": .., "Dim": .. }</c>). Shared by every mode that places something
    /// in the world; hand-rolled copies used to drop the dimension and collide across dimensions.
    /// A missing "Dim" reads as 0, matching entries written before dimensions were recorded.
    /// </summary>
    public static class JsonPos
    {
        /// <summary>Serializes a position for storage. The counterpart of <see cref="TryRead"/>.</summary>
        public static JObject Write(BlockPos pos)
            => new JObject { ["X"] = pos.X, ["Y"] = pos.Y, ["Z"] = pos.Z, ["Dim"] = pos.dimension };

        /// <summary>Same, from loose coordinates (the layout generator works in ints, not BlockPos).</summary>
        public static JObject Write(int x, int y, int z, int dim)
            => new JObject { ["X"] = x, ["Y"] = y, ["Z"] = z, ["Dim"] = dim };

        /// <summary>
        /// Reads a stored position. False for a non-object or missing X/Y/Z, so a malformed entry is
        /// skipped rather than read as (0,0,0) and placed at the world origin.
        /// </summary>
        public static bool TryRead(JToken token, out BlockPos pos)
        {
            pos = null;
            if (!(token is JObject e) || e["X"] == null || e["Y"] == null || e["Z"] == null) return false;

            pos = new BlockPos(e.Value<int>("X"), e.Value<int>("Y"), e.Value<int>("Z"), e.Value<int>("Dim"));
            return true;
        }

        /// <summary>Whether a stored entry sits at exactly this block, dimension included.</summary>
        public static bool Matches(JObject entry, BlockPos pos)
            => entry != null
               && entry.Value<int>("X") == pos.X
               && entry.Value<int>("Y") == pos.Y
               && entry.Value<int>("Z") == pos.Z
               && entry.Value<int>("Dim") == pos.dimension;
    }
}
