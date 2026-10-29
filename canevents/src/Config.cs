namespace canevents.src
{
    public class Config
    {
        public bool allowFriendlyFire = false;
        public string eventPlaceFolderName = "eventPlaces";
        public int attemptComposeTeamsEveryNSeconds = 5;
        public int attemptFindLocationForRunningEventEveryNSeconds = 5;
        // Give up on a match with no free arena after this many seconds and refund stakes. 0 = never.
        public int maxLocationSearchSeconds = 120;
        public int maxEventDurationSeconds = 300;
        public int countdownSeconds = 5;

        // GlKeys name. A player's rebind in clientsettings.json overrides it.
        public string mainDialogHotkey = "K";

        // Auto-schedule
        public bool autoScheduleEnabled = true;
        public int scheduleIntervalMinutes = 30;
        public int minPlayersToStart = 1;
        public string[] enabledGameModes = new string[] { "1x1", "spleef" };
        public int maxConcurrentEvents = 3;

        // Default /eventadmin open window length. 0 = open until the admin starts the event.
        public int signupWindowMinutes = 5;

        // Open matches (OpenDeathmatch etc.) kept running from boot; restarted if one dies.
        public string[] openEvents = new string[0];

        // Seconds after being hurt by a creature or player during which a player can't walk into or out
        // of an arena (/canlive, pads). 0 = off.
        public int combatTagSeconds = 10;

        // Disabled from the admin GUI: no new signups or auto-scheduling, running matches continue.
        public string[] disabledEventCodes = new string[0];

        // UIDs banned from all queues (/eventadmin blacklist).
        public string[] queueBlacklistedUids = new string[0];

        // Mutator keys this server forbids, e.g. ["friendlyfire", "speed"]. See EventMutators.Known.
        public string[] disabledMutators = new string[0];

        // Ranked Elo spread per match: Base, widened per second the oldest side has waited.
        public int rankedMmrBaseTolerance = 150;
        public int rankedMmrToleranceWidenPerSecond = 25;

        // Admin safety caps
        public int maxPlatformFloodFillRadius = 30;
        public int maxPlatformFloodFillBlocks = 5000;

        // Rewards
        public string rewardWinnerItemCode = "game:gear-rusty";
        public int rewardWinnerQuantity = 1;
        public bool rewardParticipation = false;
        public string rewardParticipationItemCode = "";
        public int rewardParticipationQuantity = 0;

        // Matches shorter than this pay no reward. 0 = off. Per event: RewardSpec.MinMatchSeconds.
        public int minMatchDurationSecondsForReward = 0;

        // Token bucket on the top reward: burst rewards, then one back per refillHours. 0 = off.
        public int rewardRateCapBurst = 12;
        public double rewardRateCapRefillHours = 2.0;
        // true = separate bucket per (player, event). Per event: RewardSpec.SeparateRewardCap.
        public bool rewardRateCapPerEvent = false;

        // Issued gear
        // Dropped issued kit: "cleanup" (swept at match end, needs bounds), "return", "remove", "allow".
        public string issuedGearDrop = "cleanup";

        // Entry fee / wager
        // Void fee match (draw or under the gate): refund a leaver's stake (true) or burn it (false).
        public bool entryFeeRefundForfeitOnVoid = true;

        // Ready check
        // Seconds to confirm before a queue match looks for an arena. 0 = no check.
        public int queueReadyCheckSeconds = 20;

        // Duels
        // Duels never affect Elo.
        public bool duelsEnabled = true;

        public int duelInviteTimeoutSeconds = 60;

        // Spectating
        // Seat idle admins as observers of every match that goes live.
        public bool autoSpectateAdmins = false;

        // Positions/health telemetry rate for admin observers.
        public int spectatorTelemetryIntervalMs = 500;

        // Diagnostics
        // Verbose "[trace]" log on server and clients (clients must reconnect). Noisy.
        public bool diagnosticsLogging = false;

        // Keep arena chunks loaded during a match; with no player nearby they unload in ~12s.
        // Release goes through UnloadChunkColumn, so turn off if players report the ground vanishing.
        public bool pinArenaChunks = true;

        // One SpawnParticles packet per particle; turn off if clients silently close mid-match.
        public bool arenaOutlineParticles = true;

        // Inventory audit
        // Log every inventory take/return ("[inventory]") plus a history file for admins.
        public bool inventoryAudit = true;

        // About two entries per player per match.
        public int inventoryAuditMaxEntries = 4000;

        // Above 1.2s the outline blinks (particle lifetime, see ZoneVisualizer.MakeProps).
        public double arenaOutlineIntervalSeconds = 1.0;
    }
}
