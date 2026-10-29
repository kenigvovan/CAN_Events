namespace canevents.src.EventManagers.eventBehavior.Shared
{
    public interface ILoadoutProvider
    {
        string[] GetLoadoutSetNames(string eventCode = null, LoadoutManager lm = null);
        string[] GetLoadoutSetItemCodes(string eventCode = null, LoadoutManager lm = null);
        string[] GetLoadoutSetDescriptions(string eventCode = null, LoadoutManager lm = null);
        (string name, LoadoutEntry[] items)[] GetInlineLoadoutSets();

        // False for hidden-role modes: set names are never sent to clients, they'd reveal roles.
        bool OffersPlayerChoice => true;
    }
}
