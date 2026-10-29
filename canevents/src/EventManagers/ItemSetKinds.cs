using System.Collections.Generic;

namespace canevents.src.EventManagers
{
    // Item-set stores, each a LoadoutManager over its own folder; same panel and packets, the kind picks
    // the store. A new kind is a folder name here plus a line in CANEventManager.
    public static class ItemSetKinds
    {
        // Gear handed out at match start.
        public const string Loadout = "loadout";

        // Clothes over the loadout so players can be told apart; quantities are ignored.
        public const string Outfit = "outfit";

        // kind -> folder, in admin panel order.
        public static readonly IReadOnlyDictionary<string, string> Folders =
            new Dictionary<string, string>
            {
                { Loadout, "loadouts" },
                { Outfit, "outfits" }
            };

        // An absent kind means loadouts, which is what pre-outfit clients send.
        public static string Normalize(string kind)
            => string.IsNullOrEmpty(kind) ? Loadout : kind;

        public static bool IsKnown(string kind) => Folders.ContainsKey(Normalize(kind));
    }
}
