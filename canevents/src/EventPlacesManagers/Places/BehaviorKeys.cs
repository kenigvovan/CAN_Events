namespace canevents.src.EventPlacesManagers.Places
{
    /// <summary>
    /// Keys into <see cref="EventPlace.BehaviorData"/>, centralized so producer (commands/network)
    /// and consumer (event behaviors) can't drift on a raw string literal.
    /// </summary>
    public static class BehaviorKeys
    {
        public const string Spleef = "Spleef";
        public const string Parkour = "Parkour";
        public const string KingOfTheHill = "KingOfTheHill";
        public const string HungerGames = "HungerGames";
        public const string CaptureTheFlag = "CaptureTheFlag";
        public const string MurderMystery = "MurderMystery";
    }
}
