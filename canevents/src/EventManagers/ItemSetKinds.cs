using System.Collections.Generic;

namespace canevents.src.EventManagers
{
    /// <summary>
    /// The kinds of item-set store the mod keeps, each a <see cref="LoadoutManager"/> over its own
    /// folder under ModConfig/canevents/. Both hold the same thing - named sets of item codes per
    /// event - and are edited by the same panel and the same packets; the kind picks the store.
    ///
    /// Adding a third kind (reward bundles, HG chest fills) is a folder name here plus a line in
    /// CANEventManager: the network handlers and the admin panel already work off this list.
    /// </summary>
    public static class ItemSetKinds
    {
        /// <summary>Gear handed out at match start. The historic store, folder "loadouts".</summary>
        public const string Loadout = "loadout";

        /// <summary>Plain clothes worn over the loadout, so players can be told apart and described
        /// ("the murderer wore red"). Folder "outfits"; quantities are ignored.</summary>
        public const string Outfit = "outfit";

        /// <summary>kind -> folder name. The order is the order the admin panel shows them in.</summary>
        public static readonly IReadOnlyDictionary<string, string> Folders =
            new Dictionary<string, string>
            {
                { Loadout, "loadouts" },
                { Outfit, "outfits" }
            };

        /// <summary>Normalises what arrived from a client: an absent kind means the loadout store,
        /// which is what every pre-outfit client sends.</summary>
        public static string Normalize(string kind)
            => string.IsNullOrEmpty(kind) ? Loadout : kind;

        public static bool IsKnown(string kind) => Folders.ContainsKey(Normalize(kind));
    }
}
