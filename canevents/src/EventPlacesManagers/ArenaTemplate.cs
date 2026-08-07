namespace canevents.src.EventPlacesManagers
{
    public class ArenaTemplate
    {
        public string Code;
        public string SchematicPath;
        public string[] PlaceTypes;
        public SpawnOffset[] SpawnOffsets;
        public string Description;
        public bool OnDemand; // if true, blocks are placed only while event runs
    }

    public class SpawnOffset
    {
        public int X, Y, Z;
    }
}
