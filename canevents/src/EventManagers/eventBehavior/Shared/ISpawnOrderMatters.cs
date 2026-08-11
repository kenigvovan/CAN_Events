namespace canevents.src.EventManagers.eventBehavior.Shared
{
    /// <summary>
    /// Marks a mode whose spawn points are not interchangeable: point number N is a particular team's
    /// ground, tied to something else indexed the same way (Capture the Flag's posts). Spawn points
    /// are shuffled per match by default so a half-full arena does not huddle on one arc of the ring;
    /// a mode carrying this marker is dealt its points in the order the arena was laid out in.
    ///
    /// An event can still override either way with the TPThereAndBack "randomizeSpawns" attribute.
    /// </summary>
    public interface ISpawnOrderMatters
    {
    }
}
