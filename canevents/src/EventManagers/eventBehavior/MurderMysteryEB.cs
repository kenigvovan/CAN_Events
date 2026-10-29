using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers.eventBehavior.abstracts;
using canevents.src.EventManagers.eventBehavior.Shared;
using canevents.src.EventManagers.events;
using canevents.src.EventPlacesManagers.Places;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace canevents.src.EventManagers.eventBehavior
{
    // Hidden roles: murderers (melee), sheriffs (bow), unarmed innocents.
    // Roles are loadout sets, written into PlayerLoadoutChoices before the kit is given.
    public class MurderMysteryEB : EliminatingKillScoringEB, IBotCombatRoles
    {
        private enum Role { Innocent, Sheriff, Murderer }

        private int murdererCount = 1;
        private int sheriffCount = 1;
        private int extraMurdererPerPlayers;   // 0 = off
        private string murdererSet = "Murderer";
        private string sheriffSet = "Sheriff";
        private string innocentSet = "Innocent";
        private bool innocentsCanFight;
        private bool concealWeapons = true;
        private bool lockOutfit = true;

        // "death", "disarm" or "none"
        private string sheriffPenalty = PenaltyDeath;
        private float sheriffDisarmSeconds = 20f;
        private float sheriffDisarmSlowness = 0.6f;

        // Damage overrides per hit, 0 = item's own damage. bowDamage is the fallback for both roles.
        private float bowDamage;
        private float sheriffBowDamage;
        private float murdererBowDamage;

        private float murdererMeleeDamage;

        // Only deputises innocents; a murderer stays a murderer.
        private bool pickupMakesSheriff;

        // 1 = off
        private float murdererSpeed = 1f;

        private bool sheriffDropsWeapon = true;
        private bool murdererDropsWeapon;

        // Shorten the match when nobody has died for this long, 0 = off.
        private float stalemateSeconds;

        // Time left on the clock after a stalemate fires.
        private float stalemateGraceSeconds = 60f;

        private const string PenaltyDeath = "death";
        private const string PenaltyDisarm = "disarm";
        private const string PenaltyNone = "none";

        private readonly Dictionary<string, Role> roles = new Dictionary<string, Role>();
        private readonly DamageHookSet hooks = new DamageHookSet();

        // Off unless the event config names an item to spawn.
        private readonly PickupSpawner pickups = new PickupSpawner();

        // null when lockOutfit is off
        private OutfitLock outfitLock;

        // First weapon in the murderer kit.
        private string botWeaponCode;

        private readonly HashSet<string> armedBots = new HashSet<string>();

        public MurderMysteryEB(string name, JsonObject attributes, RunningEvent runningEvent)
            : base(name, attributes, runningEvent) { }

        public override string Name => BehaviorNames.MurderMystery;

        // Roles are dealt by the mode, so no set names go to clients.
        public override bool OffersPlayerChoice => false;

        public override bool HidesIndividualScores => true;

        public override bool HidesKillerIdentity => true;

        protected override float DefaultTimeLimitSeconds => MmConfigKeys.Defaults.TimeLimitSeconds;

        protected override void ReadModeConfig()
        {
            // Keys and defaults shared with the admin panel's editor (MmSettingsBuffer).
            murdererCount = Config.Int(MmConfigKeys.MurdererCount, MmConfigKeys.Defaults.MurdererCount);
            sheriffCount = Config.Int(MmConfigKeys.SheriffCount, MmConfigKeys.Defaults.SheriffCount);
            extraMurdererPerPlayers = Config.Int(MmConfigKeys.ExtraMurdererPerPlayers, MmConfigKeys.Defaults.ExtraMurdererPerPlayers);
            murdererSet = Config.String("murdererSet", "Murderer");
            sheriffSet = Config.String("sheriffSet", "Sheriff");
            innocentSet = Config.String("innocentSet", "Innocent");
            innocentsCanFight = Config.Bool(MmConfigKeys.InnocentsCanFight, MmConfigKeys.Defaults.InnocentsCanFight);
            // Also accepts legacy true/false.
            sheriffPenalty = ParsePenalty(Config.String(MmConfigKeys.SheriffPenalty, MmConfigKeys.Defaults.SheriffPenalty));
            sheriffDisarmSeconds = Config.Float(MmConfigKeys.SheriffDisarmSeconds, MmConfigKeys.Defaults.SheriffDisarmSeconds);
            sheriffDisarmSlowness = Config.Float(MmConfigKeys.SheriffDisarmSlowness, MmConfigKeys.Defaults.SheriffDisarmSlowness);
            murdererSpeed = Config.Float(MmConfigKeys.MurdererSpeed, MmConfigKeys.Defaults.MurdererSpeed);
            sheriffDropsWeapon = Config.Bool(MmConfigKeys.SheriffDropsWeapon, MmConfigKeys.Defaults.SheriffDropsWeapon);
            murdererDropsWeapon = Config.Bool(MmConfigKeys.MurdererDropsWeapon, MmConfigKeys.Defaults.MurdererDropsWeapon);
            stalemateSeconds = Config.Float(MmConfigKeys.StalemateSeconds, MmConfigKeys.Defaults.StalemateSeconds);
            stalemateGraceSeconds = Config.Float(MmConfigKeys.StalemateGraceSeconds, MmConfigKeys.Defaults.StalemateGraceSeconds);
            concealWeapons = Config.Bool(MmConfigKeys.ConcealWeapons, MmConfigKeys.Defaults.ConcealWeapons);
            lockOutfit = Config.Bool(MmConfigKeys.LockOutfit, MmConfigKeys.Defaults.LockOutfit);
            bowDamage = Config.Float(MmConfigKeys.BowDamage, MmConfigKeys.Defaults.Damage);
            sheriffBowDamage = Config.Float(MmConfigKeys.SheriffBowDamage, MmConfigKeys.Defaults.Damage);
            murdererBowDamage = Config.Float(MmConfigKeys.MurdererBowDamage, MmConfigKeys.Defaults.Damage);
            murdererMeleeDamage = Config.Float(MmConfigKeys.MurdererMeleeDamage, MmConfigKeys.Defaults.Damage);
            pickupMakesSheriff = Config.Bool(MmConfigKeys.PickupMakesSheriff, MmConfigKeys.Defaults.PickupMakesSheriff);
            // bowPickup* keys, read through Config so WarnUnknown knows them.
            pickups.Parse(Config, MmConfigKeys.PickupPrefix);
        }

        public override void OnBeforeStart()
        {
            BeginMatch();   // also deals the outfits and clears the resolution latch
            roles.Clear();
            armedBots.Clear();

            // After BeginMatch, which resolves the wardrobe.
            outfitLock = lockOutfit ? new OutfitLock(runningEvent, this) : null;
        }

        public override void OnStart()
        {
            AssignRoles();
            botWeaponCode = ResolveBotWeaponCode();
            GiveLoadoutAndHealAll();
            ArmBotMurderers();
            foreach (var player in runningEvent.OnlineParticipants)
            {
                MarkLootableWeapon(player);
                if (concealWeapons) MatchWeapons.MoveOutOfHand(player);
                outfitLock?.Watch(player);
            }
            TellEveryoneTheirRole();

            // Hook is on the victim, so bots need one too.
            HookEveryone();

            // Cleared in CleanupRunningEvent.
            if (murdererSpeed > 0f && Math.Abs(murdererSpeed - 1f) > 0.001f)
                foreach (var player in runningEvent.OnlineParticipants)
                    if (RoleOf(player.PlayerUID) == Role.Murderer)
                        EventBroadcaster.ApplySpeed(player, EventBroadcaster.MurdererSpeedStatCode, murdererSpeed);

            pickups.OnClaimed = OnBowPickedUp;
            pickups.OnProgress = (player, carried, required) => player.SendMessage(0,
                Lang.Get("canevents:mm-bow-progress", carried, required), EnumChatType.Notification);
            pickups.Begin(runningEvent, BehaviorKeys.MurderMystery);

            if (!pickups.Enabled)
                log.Notification("Weapon spawns off: {0}. Set bowPickupItem and bowPickupReward on this "
                    + "event to turn them on.", pickups.DisabledReason);
            else if (!pickups.HasSpots)
                log.Warning("Weapon spawns configured, but place id={0} ('{1}') has no pickup spots "
                    + "marked - nothing will appear. Mark them with /canep mmpickup, or in the Murder "
                    + "Mystery section of the Arenas tab.",
                    runningEvent.Place?.Id, runningEvent.Place?.Name);
            else
                log.Notification("Weapon spawns armed: {0}", pickups.Describe());

            lastDeathAt = 0f;

            PushScoreboard();
        }

        public override bool WantsTick => true;

        // Bot bodies spawn after their chunk loads, so some don't exist at OnStart. Ticks once a second.
        public override void OnTick(float dt)
        {
            HookEveryone();
            ArmBotMurderers();
            log.Safely("bow pickups", () => pickups.Tick(runningEvent, log));
            // Catches disconnects; deaths check on their own.
            CheckForWinner();
            CheckStalemate();
        }

        private float lastDeathAt;

        private void CheckStalemate()
        {
            if (stalemateSeconds <= 0f || MatchResolved) return;
            if (runningEvent.ElapsedSeconds - lastDeathAt < stalemateSeconds) return;

            float endAt = runningEvent.ElapsedSeconds + Math.Max(0f, stalemateGraceSeconds);
            if (runningEvent.ShortenDuration(endAt, "murder mystery stalemate"))
                Announce("canevents:mm-stalemate", (int)stalemateGraceSeconds);
        }

        // IBotCombatRoles: bots aren't covered by damage hooks, so target rules live here.
        public bool MayAttack(string attackerUid, string victimUid)
        {
            if (attackerUid == null || victimUid == null || attackerUid == victimUid) return false;
            if (RoleOf(attackerUid) != Role.Murderer) return false;
            return RoleOf(victimUid) != Role.Murderer;
        }

        public bool ShouldFlee(string fleeingUid, string fromUid)
            => RoleOf(fleeingUid) != Role.Murderer && RoleOf(fromUid) == Role.Murderer;

        // Loadout skips bots, so murderer bots get their weapon here once the body exists.
        private void ArmBotMurderers()
        {
            if (botWeaponCode == null) return;

            foreach (var uid in runningEvent.ParticipantsStillInEvent)
            {
                if (!Bots.BotIdentity.IsBot(uid)) continue;
                if (RoleOf(uid) != Role.Murderer) continue;
                if (armedBots.Contains(uid)) continue;

                var body = runningEvent.BodyOf(uid) as EntityAgent;
                if (body?.RightHandItemSlot == null) continue;   // not spawned yet; caught next tick

                runningEvent.Bots?.GiveWeapon(uid, botWeaponCode);
                armedBots.Add(uid);
            }
        }

        private string ResolveBotWeaponCode()
        {
            var sets = LoadoutService.GetEffectiveSets(runningEvent, loadoutSets);
            var set = sets?.FirstOrDefault(s => string.Equals(s.Name, murdererSet, StringComparison.OrdinalIgnoreCase));
            if (set == null) return null;

            foreach (var entry in set.Items)
            {
                if (string.IsNullOrEmpty(entry.Code)) continue;
                var item = runningEvent.sapi.World.GetItem(new AssetLocation(entry.Code));
                if (MatchWeapons.IsWeapon(item)) return entry.Code;
            }
            return null;
        }

        private void HookEveryone()
        {
            // Needed for unarmed innocents or for damage overrides.
            if (innocentsCanFight && !RulesDamage) return;
            hooks.HookAll(runningEvent, runningEvent.ParticipantsStillInEvent, RuleHit, includeBots: true);
        }

        // Bots can be murderers but never sheriffs.
        private void AssignRoles()
        {
            var everyone = runningEvent.ParticipantsStillInEvent.ToList();
            var humans = everyone.Where(uid => !Bots.BotIdentity.IsBot(uid)).ToList();

            var rand = runningEvent.sapi.World.Rand;
            Tournaments.BracketBuilder.Shuffle(everyone, rand);
            Tournaments.BracketBuilder.Shuffle(humans, rand);

            int lineUp = everyone.Count;

            int murderers = murdererCount;
            if (extraMurdererPerPlayers > 0)
                murderers += lineUp / extraMurdererPerPlayers;
            murderers = Math.Max(0, murderers);
            int sheriffs = Math.Max(0, sheriffCount);

            // At least one murderer and one innocent.
            if (murderers < 1 && lineUp > 0) murderers = 1;
            if (murderers > lineUp - 1) murderers = Math.Max(1, lineUp - 1);

            var murdererUids = everyone.Take(murderers).ToList();
            foreach (var uid in murdererUids) roles[uid] = Role.Murderer;

            // Humans only, leaving room for one innocent.
            int sheriffRoom = Math.Max(0, lineUp - murderers - 1);
            var sheriffUids = humans.Where(uid => !roles.ContainsKey(uid))
                .Take(Math.Min(sheriffs, sheriffRoom))
                .ToList();
            foreach (var uid in sheriffUids) roles[uid] = Role.Sheriff;

            foreach (var uid in everyone)
                if (!roles.ContainsKey(uid)) roles[uid] = Role.Innocent;

            foreach (var uid in humans) runningEvent.PlayerLoadoutChoices[uid] = SetNameOf(roles[uid]);

            log.Notification("Murder Mystery: {0} in the line-up ({1} bot(s)), {2} murderer(s), {3} sheriff(s).",
                lineUp, lineUp - humans.Count, murdererUids.Count, sheriffUids.Count);
        }

        private void MarkLootableWeapon(IServerPlayer player)
        {
            var role = RoleOf(player.PlayerUID);
            bool lootable = role == Role.Sheriff ? sheriffDropsWeapon
                : role == Role.Murderer && murdererDropsWeapon;
            if (lootable) MatchWeapons.MarkLootable(player);
        }

        private string SetNameOf(Role role) => role switch
        {
            Role.Murderer => murdererSet,
            Role.Sheriff => sheriffSet,
            _ => innocentSet
        };

        private void TellEveryoneTheirRole()
        {
            int murderers = roles.Count(kvp => kvp.Value == Role.Murderer);

            foreach (var player in runningEvent.OnlineParticipants)
            {
                if (!roles.TryGetValue(player.PlayerUID, out var role)) continue;
                string message = role switch
                {
                    Role.Murderer => Lang.Get("canevents:mm-role-murderer", murderers),
                    Role.Sheriff => Lang.Get("canevents:mm-role-sheriff"),
                    _ => Lang.Get("canevents:mm-role-innocent")
                };
                player.SendMessage(0, message, EnumChatType.Notification);
            }
        }

        // Called for bots too.
        protected override void OnScoredDeath(string victimUid, Entity victimBody, KillInfo kill)
            => PunishSheriff(victimUid, kill.HasKiller ? kill.KillerUid : null, kill.KillerName);

        private void PunishSheriff(string victimUid, string killerUid, string killerName)
        {
            if (sheriffPenalty == PenaltyNone || killerUid == null || killerUid == victimUid) return;
            if (RoleOf(victimUid) != Role.Innocent) return;
            if (RoleOf(killerUid) != Role.Sheriff) return;

            if (sheriffPenalty == PenaltyDisarm)
            {
                DisarmSheriff(killerUid);
                return;
            }

            // Damage, not Eliminate, so the death goes through the normal death path.
            var body = runningEvent.BodyOf(killerUid);
            body?.ReceiveDamage(new DamageSource
            {
                Source = EnumDamageSource.Internal,
                Type = EnumDamageType.Injury
            }, 9999f);

            Announce("canevents:mm-sheriff-penalty", killerName ?? NameOf(killerUid));
        }

        // Told privately, a broadcast would reveal the sheriff.
        private void DisarmSheriff(string sheriffUid)
        {
            var player = runningEvent.sapi.World.PlayerByUid(sheriffUid) as IServerPlayer;
            if (player == null) return;

            var taken = MatchWeapons.TakeAll(player);
            if (taken.Count > 0)
            {
                runningEvent.sapi.Event.RegisterCallback(_ =>
                {
                    // Survivors stay IsActive after the end and already have their real inventory back.
                    if (runningEvent.EventState != EventState.Running) return;
                    var pl = runningEvent.sapi.World.PlayerByUid(sheriffUid) as IServerPlayer;
                    if (pl?.Entity == null || !pl.Entity.Alive) return;
                    if (!runningEvent.IsActive(sheriffUid)) return;
                    foreach (var stack in taken) InventoryUtil.Give(runningEvent.sapi, pl, stack, "mm-rearm");
                    if (concealWeapons) MatchWeapons.MoveOutOfHand(pl);
                    EventBroadcaster.ClearSpeed(pl, EventBroadcaster.DisarmSlowStatCode);
                    pl.SendMessage(0, Lang.Get("canevents:mm-sheriff-rearmed"), EnumChatType.Notification);
                }, (int)(sheriffDisarmSeconds * 1000));
            }

            if (sheriffDisarmSlowness > 0f && sheriffDisarmSlowness < 1f)
                EventBroadcaster.ApplySpeed(player, EventBroadcaster.DisarmSlowStatCode, sheriffDisarmSlowness);

            player.SendMessage(0,
                Lang.Get("canevents:mm-sheriff-disarmed", (int)sheriffDisarmSeconds),
                EnumChatType.Notification);
        }

        private static string ParsePenalty(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return PenaltyDeath;
            var v = value.Trim();
            if (v.Equals(PenaltyDisarm, StringComparison.OrdinalIgnoreCase)) return PenaltyDisarm;
            if (v.Equals(PenaltyNone, StringComparison.OrdinalIgnoreCase)
                || v.Equals("false", StringComparison.OrdinalIgnoreCase)) return PenaltyNone;
            return PenaltyDeath;   // "death", "true" and anything unknown
        }

        protected override void AfterEliminated(string victimUid, Entity victimBody, KillInfo kill)
        {
            lastDeathAt = runningEvent.ElapsedSeconds;
            CheckForWinner();
        }

        // Base "last group standing" can't resolve an innocent win, so both sides are checked here.
        private void CheckForWinner()
        {
            if (MatchResolved) return;
            // Before roles are dealt everyone reads as innocent.
            if (roles.Count == 0 || runningEvent.EventState != EventState.Running) return;

            var alive = runningEvent.ParticipantsStillInEvent;
            var aliveMurderers = alive.Where(uid => RoleOf(uid) == Role.Murderer).ToList();
            var aliveInnocents = alive.Where(uid => RoleOf(uid) != Role.Murderer).ToList();

            if (aliveMurderers.Count == 0)
            {
                ResolveMatch(SurvivingSide(Role.Innocent), "murderers down");
                return;
            }
            if (aliveInnocents.Count == 0)
            {
                ResolveMatch(SurvivingSide(Role.Murderer), "everyone silenced");
                return;
            }

            // Only bots left: end it instead of letting them run out the clock.
            if (!alive.Any(uid => !Bots.BotIdentity.IsBot(uid)))
                ResolveMatch(SurvivingSide(Role.Murderer), "no players left to decide it");
        }

        // Includes dead members of the side.
        private IEnumerable<string> SurvivingSide(Role side)
            => runningEvent.ParticipantsBefore.Where(uid => side == Role.Murderer
                ? RoleOf(uid) == Role.Murderer
                : RoleOf(uid) != Role.Murderer);

        // Time up means the town wins. Manager is already finishing, hence requestFinish: false.
        public override void OnBeforeFinish()
        {
            if (MatchResolved) return;
            ResolveMatch(SurvivingSide(Role.Innocent), "time up", requestFinish: false);
        }

        public override void OnParticipantJoinedGame()
        {
            // Reconnect gives a new entity and inventory.
            hooks.RehookAll(runningEvent);
            foreach (var player in runningEvent.OnlineParticipants)
                outfitLock?.Rewatch(player);
            HookEveryone();   // decides for itself whether this mode hooks at all
        }

        public override void OnEnd()
        {
            hooks.UnhookAll();   // a leaked hook mutes damage in the next match
            outfitLock?.UnwatchAll();

            // Remove unclaimed tokens.
            log.Safely("bow pickup cleanup", () => pickups.End(runningEvent));

            var murderers = runningEvent.ParticipantsBefore
                .Where(uid => RoleOf(uid) == Role.Murderer)
                .Select(NameOf)
                .ToList();
            if (murderers.Count > 0) Announce("canevents:mm-reveal", string.Join(", ", murderers));

            bool murderersWon = runningEvent.GetWinners().Any(uid => RoleOf(uid) == Role.Murderer);
            Announce(murderersWon ? "canevents:mm-win-murderers" : "canevents:mm-win-innocents");
        }

        private void OnBowPickedUp(IServerPlayer player, List<ItemStack> given)
        {
            if (player == null) return;

            bool deputised = false;
            if (pickupMakesSheriff && RoleOf(player.PlayerUID) == Role.Innocent)
            {
                roles[player.PlayerUID] = Role.Sheriff;
                deputised = true;
            }

            if (sheriffDropsWeapon && RoleOf(player.PlayerUID) == Role.Sheriff)
                MatchWeapons.MarkLootable(player);
            if (concealWeapons) MatchWeapons.MoveOutOfHand(player);

            player.SendMessage(0, Lang.Get(deputised
                ? "canevents:mm-bow-found-sheriff"
                : "canevents:mm-bow-found"), EnumChatType.Notification);
        }

        // Ranged hit: SourceEntity is the arrow, cause entity is the archer.
        private float RuledDamage(float damage, string attackerUid, DamageSource source)
        {
            bool ranged = source?.SourceEntity != null
                          && !ReferenceEquals(source.SourceEntity, source.GetCauseEntity());

            var role = RoleOf(attackerUid);
            float ruled = ranged
                ? (role == Role.Sheriff ? sheriffBowDamage
                    : role == Role.Murderer ? murdererBowDamage : 0f)
                : (role == Role.Murderer ? murdererMeleeDamage : 0f);

            // per-role, then bowDamage, then item damage
            if (ruled <= 0f && ranged) ruled = bowDamage;
            return ruled > 0f ? ruled : damage;
        }

        private bool RulesDamage
            => bowDamage > 0f || sheriffBowDamage > 0f || murdererBowDamage > 0f || murdererMeleeDamage > 0f;

        private Role RoleOf(string uid)
            => uid != null && roles.TryGetValue(uid, out var r) ? r : Role.Innocent;

        private string NameOf(string uid) => runningEvent.NameOf(uid);

        // Player attackers only; bots follow IBotCombatRoles.
        private float RuleHit(string victimUid, float damage, DamageSource source)
        {
            if (!DamageHookSet.TryHostileHit(runningEvent, victimUid, damage, source, out var attackerUid)) return damage;
            if (!innocentsCanFight && RoleOf(attackerUid) == Role.Innocent) return 0f;
            return RuledDamage(damage, attackerUid, source);
        }
    }
}
