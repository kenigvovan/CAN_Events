namespace canevents.src
{
    public class Config
    {
        public bool allowFriendlyFire = false;
        public string eventPlaceFolderName = "eventPlaces";
        public int attemptComposeTeamsEveryNSeconds = 5;
        public int attemptFindLocationForRunningEventEveryNSeconds = 5;
        // Abort an assembled match that found no free arena in this many seconds, refunding stakes. 0 = never.
        public int maxLocationSearchSeconds = 120;
        public int maxEventDurationSeconds = 300;
        public int countdownSeconds = 5;

        // Default key for the Events Browser (GlKeys name). Only a default: a player's own rebind
        // in clientsettings.json wins over it.
        public string mainDialogHotkey = "K";

        // Auto-schedule
        public bool autoScheduleEnabled = true;
        public int scheduleIntervalMinutes = 30;
        public int minPlayersToStart = 1;
        public string[] enabledGameModes = new string[] { "1x1", "spleef" };
        public int maxConcurrentEvents = 3;

        // Default length of an /eventadmin open registration window when no minutes are given.
        // 0 means the window stays open until the admin starts the event themselves.
        public int signupWindowMinutes = 5;

        // Events disabled from the admin GUI: blocks new registrations and auto-scheduling, running
        // matches continue. Runtime-toggled, persisted.
        public string[] disabledEventCodes = new string[0];

        // Player UIDs banned from all event queues (/eventadmin blacklist). Runtime-toggled, persisted.
        public string[] queueBlacklistedUids = new string[0];

        // Mutators this server does not allow, by key: e.g. ["friendlyfire", "speed"]. A disabled
        // mutator is dropped from event JSONs at load (with a log line), refused in duel challenges,
        // and its knob is hidden from the duel tab. See EventMutators.Known for the full list.
        public string[] disabledMutators = new string[0];

        // Ranked matchmaking: Elo spread allowed within a match, starting at Base and widening per
        // second the oldest waiting side has queued, so nobody waits forever.
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

        // Anti-throw: matches shorter than this pay no reward. 0 = off; overridable per event via
        // RewardSpec.MinMatchSeconds.
        public int minMatchDurationSecondsForReward = 0;

        // Anti-farm token bucket on the top reward: <burst> rewards, then one slot back per
        // <refillHours>. 0 burst = disabled. Default ≈ 12 winner rewards/day.
        public int rewardRateCapBurst = 12;
        public double rewardRateCapRefillHours = 2.0;
        // false = one bucket per player across all events; true = a bucket per (player, event).
        // Overridable per event via RewardSpec.SeparateRewardCap.
        public bool rewardRateCapPerEvent = false;

        // Issued gear
        // What happens when a player throws match-issued kit on the ground. Death never drops it
        // (the inventory is emptied before the body is), so this is about the drop key alone -
        // without which a thrown sword lies on the arena for good and the match mints it.
        //   "cleanup" - dropping works as before, marked items are swept off the arena when the
        //               match ends. Needs a place with marked bounds.
        //   "return"  - handed straight back to whoever dropped it
        //   "remove"  - destroyed on the spot
        //   "allow"   - left alone
        // Overridable per event via the SavePlayerAttributes behavior's "issuedGearDrop" attribute.
        // Only ever touches issued kit and outfits: an item the SERVER dropped for a player (their
        // inventory was full) is never touched, or a reward landing at their feet would vanish.
        public string issuedGearDrop = "cleanup";

        // Entry fee / wager
        // On a void fee match (draw or under the contribution-gate), refund a disconnector's stake
        // (true) or burn it (false). On a real result a disconnect always forfeits it (anti-dodge).
        public bool entryFeeRefundForfeitOnVoid = true;

        // Ready check
        // Seconds a queue match waits for its players to confirm they are at the keyboard before it
        // looks for an arena. A queue slot is taken minutes in advance and people wander off; without
        // this the match started with them, costing their side a player and them a loss they never
        // played. 0 disables the check entirely.
        public int queueReadyCheckSeconds = 20;

        // Duels
        // Player-to-player challenges. A duel never affects Elo whatever it is played for: the
        // players pick their own opponent, so a rated one would just be two people trading wins.
        public bool duelsEnabled = true;

        // How long a challenge waits for an answer before it lapses.
        public int duelInviteTimeoutSeconds = 60;

        // Spectating
        // Seat idle admins as observers of every match that goes live. Off by default: it moves a
        // player without them asking. They can always watch on demand with /canspectate.
        public bool autoSpectateAdmins = false;

        // How often (ms) an observer's client is sent participant positions and health. Only
        // observers with the admin privilege receive it, and only while at least one is watching.
        public int spectatorTelemetryIntervalMs = 500;
    }
}
