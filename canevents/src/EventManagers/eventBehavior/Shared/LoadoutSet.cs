using System.Collections.Generic;
using Newtonsoft.Json;

namespace canevents.src.EventManagers.eventBehavior.Shared
{
    // One level of a kit item's upgrade chain, bought with match shop points. Code takes the item's place,
    // the extras come along (a sling with its stones) and go again with the next level.
    public class UpgradeStep
    {
        public string Code;
        // 0 = as many as the kit item.
        public int Quantity;
        public int Cost;
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public List<UpgradeExtra> Extras;
    }

    public class UpgradeExtra
    {
        public string Code;
        public int Quantity;
    }

    public class LoadoutEntry
    {
        public string Code;
        public int Quantity;

        // Managed kits only: stable across edits, so bought upgrade levels stay on the right item.
        public string Id;
        public List<UpgradeStep> Upgrades;

        // Match shop: one-time purchase id (kept across rounds while carried), null for kit items.
        public string ShopItem;
        public bool DropOnDeath;
    }

    public class LoadoutSet
    {
        public string Name;
        public List<LoadoutEntry> Items = new List<LoadoutEntry>();
    }
}
