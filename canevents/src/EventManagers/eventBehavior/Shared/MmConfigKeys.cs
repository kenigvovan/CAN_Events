namespace canevents.src.EventManagers.eventBehavior.Shared
{
    // Murder Mystery keys shared by the behavior and the admin panel. bowPickup* defaults live in PickupSpawner.
    public static class MmConfigKeys
    {
        // "bowPickupItem", ...
        public const string PickupPrefix = "bow";

        public const string TimeLimitSeconds = BehaviorConfigKeys.TimeLimitSeconds;
        public const string MurdererCount = "murdererCount";
        public const string SheriffCount = "sheriffCount";
        public const string ExtraMurdererPerPlayers = "extraMurdererPerPlayers";
        public const string InnocentsCanFight = "innocentsCanFight";
        public const string ConcealWeapons = "concealWeapons";
        public const string LockOutfit = "lockOutfit";
        public const string SheriffPenalty = "sheriffPenalty";
        public const string SheriffDisarmSeconds = "sheriffDisarmSeconds";
        public const string SheriffDisarmSlowness = "sheriffDisarmSlowness";
        public const string MurdererSpeed = "murdererSpeed";
        public const string SheriffDropsWeapon = "sheriffDropsWeapon";
        public const string MurdererDropsWeapon = "murdererDropsWeapon";
        public const string StalemateSeconds = "stalemateSeconds";
        public const string StalemateGraceSeconds = "stalemateGraceSeconds";
        public const string BowDamage = "bowDamage";
        public const string SheriffBowDamage = "sheriffBowDamage";
        public const string MurdererBowDamage = "murdererBowDamage";
        public const string MurdererMeleeDamage = "murdererMeleeDamage";
        public const string PickupMakesSheriff = PickupPrefix + "PickupMakesSheriff";

        public static class Defaults
        {
            public const float TimeLimitSeconds = 600f;
            public const int MurdererCount = 1;
            public const int SheriffCount = 1;
            public const int ExtraMurdererPerPlayers = 0;
            public const bool InnocentsCanFight = false;
            public const bool ConcealWeapons = true;
            public const bool LockOutfit = true;
            public const string SheriffPenalty = "death";
            public const float SheriffDisarmSeconds = 20f;
            public const float SheriffDisarmSlowness = 0.6f;
            public const float MurdererSpeed = 1f;
            public const bool SheriffDropsWeapon = true;
            public const bool MurdererDropsWeapon = false;
            public const float StalemateSeconds = 0f;
            public const float StalemateGraceSeconds = 60f;
            // 0 = weapon's own damage.
            public const float Damage = 0f;
            public const bool PickupMakesSheriff = false;
        }

        // Explicit list: loadouts and outfits share the same object and are edited elsewhere.
        public static readonly string[] Editable =
        {
            TimeLimitSeconds,
            MurdererCount,
            SheriffCount,
            ExtraMurdererPerPlayers,
            InnocentsCanFight,
            ConcealWeapons,
            LockOutfit,
            SheriffPenalty,
            SheriffDisarmSeconds,
            SheriffDisarmSlowness,
            MurdererSpeed,
            SheriffDropsWeapon,
            MurdererDropsWeapon,
            StalemateSeconds,
            StalemateGraceSeconds,
            BowDamage,
            SheriffBowDamage,
            MurdererBowDamage,
            MurdererMeleeDamage,
            PickupPrefix + PickupSpawner.ItemKey,
            PickupPrefix + PickupSpawner.QuantityKey,
            PickupPrefix + PickupSpawner.RequiredKey,
            PickupPrefix + PickupSpawner.FirstSecondsKey,
            PickupPrefix + PickupSpawner.IntervalSecondsKey,
            PickupPrefix + PickupSpawner.MaxActiveKey,
            PickupPrefix + PickupSpawner.MinDistanceKey,
            PickupPrefix + PickupSpawner.LifetimeSecondsKey,
            PickupMakesSheriff,
            PickupPrefix + PickupSpawner.RewardKey
        };

        public static bool IsEditable(string key)
        {
            if (key == null) return false;
            foreach (var k in Editable) if (k == key) return true;
            return false;
        }

        // Keys missing from settings are kept, so an older panel can't erase them.
        public static void CopyEditable(Newtonsoft.Json.Linq.JObject target, Newtonsoft.Json.Linq.JObject settings)
        {
            foreach (var prop in settings.Properties())
                if (IsEditable(prop.Name)) target[prop.Name] = prop.Value.DeepClone();
        }
    }
}
