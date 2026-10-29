namespace canevents.src.EventManagers.eventBehavior.Shared
{
    // A mode that runs without a queue: started by an admin or at boot, players walk in and out.
    public interface IOpenMatchMode
    {
        int Teams { get; }

        // Largest allowed gap between the biggest and smallest team after a join. 0 = no limit.
        int MaxImbalance { get; }
    }
}
