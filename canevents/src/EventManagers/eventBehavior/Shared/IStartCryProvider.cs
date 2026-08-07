namespace canevents.src.EventManagers.eventBehavior.Shared
{
    // Overrides the client-side "start cry" line. Resolved at SendStateUpdate time; first wins.
    public interface IStartCryProvider
    {
        string StartCryLangKey { get; }
    }
}
