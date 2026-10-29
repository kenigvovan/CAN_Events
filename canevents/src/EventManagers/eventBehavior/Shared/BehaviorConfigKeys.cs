namespace canevents.src.EventManagers.eventBehavior.Shared
{
    // Keys read by more than one mode or by the admin panel.
    public static class BehaviorConfigKeys
    {
        public const string TimeLimitSeconds = "timeLimitSeconds";
        public const string PrefightSeconds = "prefightSeconds";
        public const string ScoreLimit = "scoreLimit";
        public const string ShowZoneParticles = "showZoneParticles";
        public const string AnnounceDeaths = "announceDeaths";
        public const string ScoreboardVisibility = "scoreboardVisibility";
        // First is the default.
        public static readonly string[] ScoreboardVisibilities = { "all", "eliminated", "spectators", "none" };
        public const string LoadoutSets = "loadoutSets";
        public const string Outfits = "outfits";

        public const float HungerGamesTimeLimitSeconds = 900f;
    }
}
