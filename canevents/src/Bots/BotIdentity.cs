using System;

namespace canevents.src.Bots
{
    /// <summary>
    /// How a bot is told apart from a player wherever the match deals in uids. Match logic works on
    /// plain uid strings, so a bot only needs its own uid; parts that assume a real account
    /// (leaderboard, Elo, rewards, entry-fee escrow) ask <see cref="IsBot"/> and skip.
    /// </summary>
    public static class BotIdentity
    {
        /// <summary>Prefix that makes a uid a bot uid. Real Vintagestory uids never contain ':'.</summary>
        public const string UidPrefix = "bot:";

        public static bool IsBot(string uid)
            => uid != null && uid.StartsWith(UidPrefix, StringComparison.Ordinal);

        /// <summary>A fresh uid for one bot in one match. Never persisted anywhere.</summary>
        public static string NewUid(int index) => UidPrefix + Guid.NewGuid().ToString("N").Substring(0, 8) + "-" + index;

        // Deliberately not player-like: a bot must not be mistaken for a person in feeds/results.
        private static readonly string[] Names =
        {
            "Aspen", "Birch", "Cedar", "Dahlia", "Ember", "Flint", "Garnet", "Hazel",
            "Indigo", "Juniper", "Kestrel", "Larch", "Marlow", "Nettle", "Onyx", "Poppy",
            "Quill", "Rowan", "Sable", "Thistle", "Umber", "Vesper", "Willow", "Yarrow"
        };

        /// <summary>Display name for the bot at <paramref name="index"/> in this match, unique within it.</summary>
        public static string NameFor(int index)
        {
            string name = Names[index % Names.Length];
            int round = index / Names.Length;
            return round == 0 ? name : name + " " + (round + 1);
        }

        /// <summary>
        /// The name of a bot uid, derived from the uid's trailing "-N" (see <see cref="NewUid"/>) so
        /// every part of the mod agrees without shared state. Per-site counters used to disagree:
        /// the bracket numbered by place in the field, the arena by place in the match.
        /// </summary>
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
