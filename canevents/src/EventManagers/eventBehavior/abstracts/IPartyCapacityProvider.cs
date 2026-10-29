namespace canevents.src.EventManagers.eventBehavior.abstracts
{
    public interface IPartyCapacityProvider
    {
        int GetMaxPlayers();
        int GetMinPlayers();
    }
}
