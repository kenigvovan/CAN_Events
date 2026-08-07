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
    }
}
