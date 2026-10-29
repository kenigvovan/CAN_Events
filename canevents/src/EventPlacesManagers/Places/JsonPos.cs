using Newtonsoft.Json.Linq;
using Vintagestory.API.MathTools;

namespace canevents.src.EventPlacesManagers.Places
{
    // { "X", "Y", "Z", "Dim" } in behavior data. Missing "Dim" reads as 0 (older entries).
    public static class JsonPos
    {
        public static JObject Write(BlockPos pos)
            => new JObject { ["X"] = pos.X, ["Y"] = pos.Y, ["Z"] = pos.Z, ["Dim"] = pos.dimension };

        public static JObject Write(int x, int y, int z, int dim)
            => new JObject { ["X"] = x, ["Y"] = y, ["Z"] = z, ["Dim"] = dim };

        // Keeps the target's other keys (radius, height).
        public static void WriteInto(JObject target, BlockPos pos)
        {
            target["X"] = pos.X;
            target["Y"] = pos.Y;
            target["Z"] = pos.Z;
            target["Dim"] = pos.dimension;
        }

        // False on missing X/Y/Z, so bad entries aren't read as (0,0,0).
        public static bool TryRead(JToken token, out BlockPos pos)
        {
            pos = null;
            if (!(token is JObject e) || e["X"] == null || e["Y"] == null || e["Z"] == null) return false;

            pos = new BlockPos(e.Value<int>("X"), e.Value<int>("Y"), e.Value<int>("Z"), e.Value<int>("Dim"));
            return true;
        }

        public static bool Matches(JObject entry, BlockPos pos)
            => entry != null
               && entry.Value<int>("X") == pos.X
               && entry.Value<int>("Y") == pos.Y
               && entry.Value<int>("Z") == pos.Z
               && entry.Value<int>("Dim") == pos.dimension;
    }
}
