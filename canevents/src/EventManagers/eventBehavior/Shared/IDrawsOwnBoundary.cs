namespace canevents.src.EventManagers.eventBehavior.Shared
{
    /// <summary>
    /// Implemented by a mode that already draws the edge of its own playable area, so the match
    /// driver does not outline the arena footprint on top of it.
    ///
    /// The distinction is between "the edge that matters right now" and "the walls of the arena".
    /// Hunger Games has a storm ring that shrinks: its footprint is the whole map and outlining it
    /// would tell players nothing while contradicting the ring they must actually stay inside.
    /// King of the Hill and Capture the Flag draw a hill and capture radii - those are objectives
    /// inside the arena, not its boundary, so those modes still want the footprint drawn.
    /// </summary>
    public interface IDrawsOwnBoundary
    {
    }
}
