using System.Collections.Generic;

namespace canevents.src.EventManagers.eventBehavior.Shared
{
    public class LoadoutEntry
    {
        public string Code;
        public int Quantity;
    }

    public class LoadoutSet
    {
        public string Name;
        public List<LoadoutEntry> Items = new List<LoadoutEntry>();
    }
}
