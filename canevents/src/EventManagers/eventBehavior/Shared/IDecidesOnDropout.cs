namespace canevents.src.EventManagers.eventBehavior.Shared
{
    // A disconnect only takes the player out of play; the mode decides what that means for the match
    // (Parkour race, battle rounds), instead of the last-team-standing check ending it on the spot.
    public interface IDecidesOnDropout
    {
    }
}
