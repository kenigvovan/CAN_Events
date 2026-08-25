namespace canevents.src.EventManagers.eventBehavior.Shared
{
    /// <summary>
    /// Which Murder Mystery attributes the admin panel may read and write.
    ///
    /// A list rather than "everything in the behavior's attributes" on purpose: loadout sets and the
    /// outfit wardrobe live in the same object and are edited elsewhere, and a panel that round-trips
    /// the whole blob would quietly drop whatever its version did not know about. Both ends read this
    /// one list, so adding a setting is a single edit here.
    /// </summary>
    public static class MmConfigKeys
    {
        public static readonly string[] Editable =
        {
            "timeLimitSeconds",
            "murdererCount",
            "sheriffCount",
            "extraMurdererPerPlayers",
            "innocentsCanFight",
            "concealWeapons",
            "lockOutfit",
            "sheriffPenalty",
            "sheriffDisarmSeconds",
            "sheriffDisarmSlowness",
            "murdererSpeed",
            "sheriffDropsWeapon",
            "murdererDropsWeapon",
            "stalemateSeconds",
            "stalemateGraceSeconds",
            "bowDamage",
            "sheriffBowDamage",
            "murdererBowDamage",
            "murdererMeleeDamage",
            "bowPickupItem",
            "bowPickupQuantity",
            "bowPickupRequired",
            "bowPickupFirstSeconds",
            "bowPickupIntervalSeconds",
            "bowPickupMaxActive",
            "bowPickupMinDistance",
            "bowPickupLifetimeSeconds",
            "bowPickupMakesSheriff",
            "bowPickupReward"
        };

        public static bool IsEditable(string key)
        {
            if (key == null) return false;
            foreach (var k in Editable) if (k == key) return true;
            return false;
        }
    }
}
