namespace canevents.src.EventManagers.eventBehavior.abstracts
{
    /// <summary>
    /// Reports how many players an event needs/allows. Discovered via
    /// <c>Behaviors.OfType&lt;IPartyCapacityProvider&gt;()</c>, so bracket/tournament events can
    /// compute capacity differently from the party-assembling math.
    /// </summary>
    public interface IPartyCapacityProvider
    {
        int GetMaxPlayers();
        int GetMinPlayers();
    }
}
