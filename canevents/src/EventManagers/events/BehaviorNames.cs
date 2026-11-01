namespace canevents.src.EventManagers.events
{
    // Behavior "name" values in event JSON, as registered in canevents.StartServerSide.
    public static class BehaviorNames
    {
        public const string SavePlayerAttributes = "SavePlayerAttributes";
        public const string FullPartyRequired = "FullPartyRequired";
        public const string ByPartsPartyCollected = "ByPartsPartyCollected";
        public const string TPThereAndBack = "TPThereAndBack";
        public const string ArenaFight = "ArenaFight";
        public const string NoFriendlyFire = "NoFriendlyFire";
        public const string Spleef = "Spleef";
        public const string ShrinkingSpleef = "ShrinkingSpleef";
        public const string Parkour = "Parkour";
        public const string Deathmatch = "Deathmatch";
        public const string KingOfTheHill = "KingOfTheHill";
        public const string HungerGames = "HungerGames";
        public const string Juggernaut = "Juggernaut";
        public const string CaptureTheFlag = "CaptureTheFlag";
        public const string MurderMystery = "MurderMystery";
        public const string ArenaBots = "ArenaBots";
        public const string OpenDeathmatch = "OpenDeathmatch";
        public const string WaitingRoom = "WaitingRoom";
        public const string BattleRounds = "BattleRounds";
        public const string ArenaProtection = "ArenaProtection";
        public const string TeamMarkers = "TeamMarkers";
        public const string MatchShop = "MatchShop";
        public const string Waves = "Waves";
        public const string MobRaids = "MobRaids";

        // Claimant id for refused block edits, not a behavior name.
        public const string HungerGamesClaimant = "hungergames";
    }
}
