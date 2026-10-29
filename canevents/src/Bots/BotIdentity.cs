using System;

namespace canevents.src.Bots
{
    // Bots are just uids with a prefix. Leaderboard, Elo, rewards and escrow skip them via IsBot.
    public static class BotIdentity
    {
        // Real VS uids never contain ':'.
        public const string UidPrefix = "bot:";

        public static bool IsBot(string uid)
            => uid != null && uid.StartsWith(UidPrefix, StringComparison.Ordinal);

        // Not persisted.
        public static string NewUid(int index) => UidPrefix + Guid.NewGuid().ToString("N").Substring(0, 8) + "-" + index;

        // Intentionally not human names, so bots aren't mistaken for players.
        private static readonly string[] Names =
        {
            "Astrolabe", "Ballast", "Caliper", "Dynamo", "Ecliptic", "Fathom", "Gantry", "Halyard",
            "Isobar", "Jetty", "Keel", "Latitude", "Meridian", "Nadir", "Octant", "Plumbline",
            "Quadrant", "Rudder", "Spinnaker", "Transit", "Undertow", "Vernier", "Windlass", "Yardarm"
        };

        public static string NameFor(int index)
        {
            string name = Names[index % Names.Length];
            int round = index / Names.Length;
            return round == 0 ? name : name + " " + (round + 1);
        }

        // From the uid's "-N" suffix, so bracket, arena and feeds all show the same name.
        public static string NameOfUid(string uid)
        {
            if (!IsBot(uid)) return uid;
            int dash = uid.LastIndexOf('-');
            return dash >= 0 && int.TryParse(uid.Substring(dash + 1), out int index)
                ? NameFor(index)
                : "Bot";
        }
    }
}
