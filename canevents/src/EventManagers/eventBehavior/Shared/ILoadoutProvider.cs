namespace canevents.src.EventManagers.eventBehavior.Shared
{
    // Implemented by behaviors that equip players from named loadout sets, so NetworkHandler can
    // discover them without casting to a specific mode class.
    public interface ILoadoutProvider
    {
        string[] GetLoadoutSetNames(string eventCode = null, LoadoutManager lm = null);
        string[] GetLoadoutSetItemCodes(string eventCode = null, LoadoutManager lm = null);
        string[] GetLoadoutSetDescriptions(string eventCode = null, LoadoutManager lm = null);
        (string name, LoadoutEntry[] items)[] GetInlineLoadoutSets();

        /// <summary>
        /// Whether the player picks their own set. False when the mode assigns them (hidden-role
        /// modes such as Murder Mystery): then the set names are never offered at register time,
        /// never popped up at match start, and never published in the event list - the names alone
        /// would give the roles away.
        /// </summary>
        bool OffersPlayerChoice => true;
    }
}
