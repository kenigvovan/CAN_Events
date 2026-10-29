namespace canevents.src.EventManagers.eventBehavior.Shared
{
    // A mode played in rounds: the HUD timer shows the current round instead of the whole match.
    public interface IRoundClock
    {
        // False between rounds; the HUD then keeps the match clock.
        bool RoundLive { get; }
        float RoundSecondsLeft { get; }
        float RoundLength { get; }
    }
}
