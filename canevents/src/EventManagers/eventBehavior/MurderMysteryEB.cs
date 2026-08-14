using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers.eventBehavior.abstracts;
using canevents.src.EventManagers.eventBehavior.Shared;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace canevents.src.EventManagers.eventBehavior
{
    /// <summary>
    /// Hidden-role social mode. A few murderers with melee weapons and a few sheriffs with bows
    /// hide among unarmed innocents; nobody is told anyone else's role. Murderers win by killing
    /// everyone else, the rest win by killing every murderer. A sheriff who shoots an innocent
    /// dies for it, so the bow is a commitment rather than a licence.
    ///
    /// The roles ARE the loadout sets: the assignment is written into PlayerLoadoutChoices before
    /// the kit is handed out, and the three secrecy leaks that would otherwise give it away are
    /// closed by the base plumbing - OffersPlayerChoice (no set names anywhere on the wire),
    /// HidesIndividualScores (no kill tallies in the HUD) and HidesKillerIdentity (deaths are
    /// announced, killers are not - on the bot death path too, which writes its own messages).
    /// </summary>
    public class MurderMysteryEB : KillScoringEB, IBotCombatRoles
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

        /// <summary>What happens to a sheriff who shoots an innocent: "death", "disarm" or "none".</summary>
        private string sheriffPenalty = PenaltyDeath;
        private float sheriffDisarmSeconds = 20f;
        private float sheriffDisarmSlowness = 0.6f;

        /// <summary>Walk-speed multiplier for murderers. A small edge in a chase, and a tell for
        /// anyone paying attention. 1 = off.</summary>
        private float murdererSpeed = 1f;

        /// <summary>Whose weapon is left on the ground for anyone to pick up when they die. A dropped
        /// bow turns whoever finds it into the next sheriff; a dropped knife hands the town a way to
        /// fight back - or hands the second murderer a spare.</summary>
        private bool sheriffDropsWeapon = true;
        private bool murdererDropsWeapon;

        /// <summary>Cut the match short when nobody has died for this long (0 = off): a murderer who
        /// hides in a corner should not be able to run the clock out on everyone.</summary>
        private float stalemateSeconds;

        /// <summary>How much time a stalemate leaves on the clock once it fires.</summary>
        private float stalemateGraceSeconds = 60f;

        private const string PenaltyDeath = "death";
        private const string PenaltyDisarm = "disarm";
        private const string PenaltyNone = "none";

        private readonly Dictionary<string, Role> roles = new Dictionary<string, Role>();
        private readonly DamageHookSet hooks = new DamageHookSet();

        /// <summary>What a murderer bot is handed, taken from the murderer kit itself.</summary>
        private string botWeaponCode;

        /// <summary>Bots already armed, so a body that arrives late is armed once and only once.</summary>
        private readonly HashSet<string> armedBots = new HashSet<string>();

        public MurderMysteryEB(string name, JsonObject attributes, RunningEvent runningEvent)
            : base(name, attributes, runningEvent) { }

        public override string Name { get => "MurderMystery"; set { } }

        // The mode deals the sets out itself; asking the player which one they want would both
        // spoil the surprise and break the murderer/sheriff head count.
        public override bool OffersPlayerChoice => false;

        // A kill count in the HUD is a confession.
        public override bool HidesIndividualScores => true;

        // Deaths are announced, killers are not - in the feed, in the chat line and on the bot
        // death path alike. Environmental deaths read the same way on purpose: being unable to tell
        // an accident from a murder is the mode working as intended.
        public override bool HidesKillerIdentity => true;

        public override void Initialize()
        {
            ParseTimeLimit(600f);
            murdererCount = Config.Int("murdererCount", 1);
            sheriffCount = Config.Int("sheriffCount", 1);
            extraMurdererPerPlayers = Config.Int("extraMurdererPerPlayers", 0);
            murdererSet = Config.String("murdererSet", "Murderer");
            sheriffSet = Config.String("sheriffSet", "Sheriff");
            innocentSet = Config.String("innocentSet", "Innocent");
            innocentsCanFight = Config.Bool("innocentsCanFight", false);
            // Reads "death"/"disarm"/"none", and still understands the plain true/false it used to be.
            sheriffPenalty = ParsePenalty(Config.String("sheriffPenalty", PenaltyDeath));
            sheriffDisarmSeconds = Config.Float("sheriffDisarmSeconds", 20f);
            sheriffDisarmSlowness = Config.Float("sheriffDisarmSlowness", 0.6f);
            murdererSpeed = Config.Float("murdererSpeed", 1f);
            sheriffDropsWeapon = Config.Bool("sheriffDropsWeapon", true);
            murdererDropsWeapon = Config.Bool("murdererDropsWeapon", false);
            stalemateSeconds = Config.Float("stalemateSeconds", 0f);
            stalemateGraceSeconds = Config.Float("stalemateGraceSeconds", 60f);
            concealWeapons = Config.Bool("concealWeapons", true);
            lockOutfit = Config.Bool("lockOutfit", true);
            ParseLoadoutSets();
            Config.WarnUnknown();
        }

        public override void OnBeforeStart()
        {
            runningEvent.SetDuration(timeLimitSeconds);
            InitScores();   // also deals the outfits and clears the resolution latch
            roles.Clear();
            armedBots.Clear();
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
                ConcealWeapon(player);
                WatchOutfit(player);
            }
            TellEveryoneTheirRole();

            // Unarmed innocents beating the murderer to death with fists would settle every match
            // by headcount. Hooked on the victim, so the whole line-up needs a hook - bots included,
            // or the innocents can still cull them and hand the murderers the win.
            HookEveryone();

            // A murderer moves a little faster: it wins chases, and it is also the one tell an alert
            // player can read off someone walking past. Cleared for everyone in CleanupRunningEvent.
            if (murdererSpeed > 0f && System.Math.Abs(murdererSpeed - 1f) > 0.001f)
                foreach (var player in runningEvent.OnlineParticipants)
                    if (RoleOf(player.PlayerUID) == Role.Murderer)
                        EventBroadcaster.ApplySpeedMutator(player, murdererSpeed);

            // A bot dies outside the scored death path, so the mode has to listen for it directly.
            runningEvent.sapi.Event.OnEntityDeath += OnAnyEntityDeath;

            lastDeathAt = 0f;

            PushScoreboard();
        }

        private float sinceHookSweep;

        // The only thing still on a timer: a bot's body arrives from a spawn callback waiting on its
        // chunk, so some bodies do not exist yet at OnStart - and there is no event to hang either
        // the damage hook or the murderer's knife on.
        public override void OnTick(float dt)
        {
            sinceHookSweep += dt;
            if (sinceHookSweep < 1f) return;
            sinceHookSweep = 0f;
            HookEveryone();
            ArmBotMurderers();
            // A bot's death never reaches AfterScoredDeath: the manager only dispatches
            // OnParticipantDeath for players, and a bot is buried by ArenaBotsEB, which just calls
            // EliminatePlayer. Kill the last murderer while it is a bot and the match would
            // otherwise run on until the clock ran out.
            CheckForWinner();
            CheckStalemate();
        }

        /// <summary>Elapsed seconds at the last death, so a quiet match can be cut short.</summary>
        private float lastDeathAt;

        /// <summary>
        /// Nobody has died for a while, so the clock gets cut. A murderer who finds a corner and
        /// waits is the classic way this mode dies of boredom: the remaining players cannot make
        /// anything happen, and the match still owes them its full length.
        /// </summary>
        private void CheckStalemate()
        {
            if (stalemateSeconds <= 0f || MatchResolved) return;
            if (runningEvent.ElapsedSeconds - lastDeathAt < stalemateSeconds) return;

            float endAt = runningEvent.ElapsedSeconds + System.Math.Max(0f, stalemateGraceSeconds);
            if (runningEvent.ShortenDuration(endAt, "murder mystery stalemate"))
                EventBroadcaster.Broadcast(runningEvent,
                    Lang.Get("canevents:mm-stalemate", (int)stalemateGraceSeconds));
        }

        // Outfit lock

        /// <summary>Live subscriptions on each player's character inventory, kept so they can be
        /// taken off again: a listener left behind would re-dress people after the match.</summary>
        private readonly Dictionary<string, (IInventory inv, Action<int> handler)> outfitWatchers
            = new Dictionary<string, (IInventory, Action<int>)>();

        /// <summary>Set while we are the ones writing the slots, so our own changes do not come
        /// back through SlotModified as another reason to dress.</summary>
        private readonly HashSet<string> dressing = new HashSet<string>();

        /// <summary>
        /// Puts an outfit piece back the moment it comes off. Nothing stops a player from opening
        /// their character inventory and dropping the clothes, and here that is not a wardrobe
        /// choice - it erases the only description anyone can give of them ("the murderer wore
        /// red"). The inventory tells us when it changed, so nothing has to be polled.
        /// </summary>
        private void WatchOutfit(IServerPlayer player)
        {
            if (!lockOutfit || outfits.Count == 0 || player == null) return;
            string uid = player.PlayerUID;
            if (outfitWatchers.ContainsKey(uid)) return;

            var inv = player.InventoryManager?.GetOwnInventory(GlobalConstants.characterInvClassName);
            if (inv == null) return;

            Action<int> handler = _ => Redress(uid);
            inv.SlotModified += handler;
            outfitWatchers[uid] = (inv, handler);
        }

        private void Redress(string uid)
        {
            if (!dressing.Add(uid)) return;

            // Deferred by a tick rather than done here: the inventory is mid-change inside its own
            // SlotModified, and writing a clothing slot from that notification is the re-entry
            // GearSlotOrderCompat exists to prevent.
            runningEvent.sapi.Event.RegisterCallback(_ =>
            {
                dressing.Remove(uid);
                if (runningEvent.EventState != EventState.Running || !runningEvent.IsActive(uid)) return;
                var pl = runningEvent.sapi.World.PlayerByUid(uid) as IServerPlayer;
                if (pl?.Entity == null || !pl.Entity.Alive) return;
                // reclaimCarried: the very piece they took off goes back on, rather than a copy -
                // otherwise stashing the coat in the backpack would leave them wearing a second one.
                // Dress only fills empty clothing slots, so nothing else is disturbed.
                EventOutfit.Dress(pl, runningEvent.sapi, outfits, OutfitIndexOf(uid), reclaimCarried: true);
            }, 50);
        }

        /// <summary>Moves the listener onto the inventory the player actually has now. No-op while
        /// it is the same one, so this is safe to call on every join.</summary>
        private void RewatchOutfit(IServerPlayer player)
        {
            if (!lockOutfit || player == null) return;
            string uid = player.PlayerUID;
            var inv = player.InventoryManager?.GetOwnInventory(GlobalConstants.characterInvClassName);

            if (outfitWatchers.TryGetValue(uid, out var existing))
            {
                if (ReferenceEquals(existing.inv, inv)) return;
                if (existing.inv != null && existing.handler != null)
                    existing.inv.SlotModified -= existing.handler;
                outfitWatchers.Remove(uid);
            }
            WatchOutfit(player);
        }

        private void UnwatchOutfits()
        {
            foreach (var kvp in outfitWatchers)
            {
                if (kvp.Value.inv != null && kvp.Value.handler != null)
                    kvp.Value.inv.SlotModified -= kvp.Value.handler;
            }
            outfitWatchers.Clear();
            dressing.Clear();
        }

        // IBotCombatRoles: bots have no damage hooks, so their restraint has to come from having no
        // legal target at all - this is the whole of the bot combat rulebook for this mode.

        /// <summary>Only murderers strike, and never each other. An innocent bot walking past its
        /// own kind must not turn the town square into a brawl.</summary>
        public bool MayAttack(string attackerUid, string victimUid)
        {
            if (attackerUid == null || victimUid == null || attackerUid == victimUid) return false;
            if (RoleOf(attackerUid) != Role.Murderer) return false;
            return RoleOf(victimUid) != Role.Murderer;
        }

        /// <summary>
        /// Everyone who cannot fight runs from a murderer they can see. Deliberately not restricted
        /// to a murderer they have "witnessed": a bot has no memory of who did what, and standing
        /// still while being stabbed reads as a broken bot rather than as a victim.
        /// </summary>
        public bool ShouldFlee(string fleeingUid, string fromUid)
            => RoleOf(fleeingUid) != Role.Murderer && RoleOf(fromUid) == Role.Murderer;

        /// <summary>
        /// Puts a knife in every murderer bot's hand. Bots are skipped by the loadout (it needs a
        /// player), so their weapon is handed over directly - and only once their body exists, which
        /// is a callback away: the spawn waits on the arena chunk.
        /// </summary>
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

        /// <summary>The first real weapon in the murderer's kit, so an armed bot carries what an
        /// armed player does without the weapon being configured twice.</summary>
        private string ResolveBotWeaponCode()
        {
            var sets = LoadoutService.GetEffectiveSets(runningEvent, loadoutSets);
            var set = sets?.FirstOrDefault(s => string.Equals(s.Name, murdererSet, StringComparison.OrdinalIgnoreCase));
            if (set == null) return null;

            foreach (var entry in set.Items)
            {
                if (string.IsNullOrEmpty(entry.Code)) continue;
                var item = runningEvent.sapi.World.GetItem(new AssetLocation(entry.Code));
                if (item?.Tool != null || (item != null && item.AttackPower > 1f)) return entry.Code;
            }
            return null;
        }

        private void HookEveryone()
        {
            if (innocentsCanFight) return;
            foreach (var uid in runningEvent.ParticipantsStillInEvent)
            {
                if (hooks.IsHooked(uid)) continue;
                HookDamage(uid);
            }
        }

        /// <summary>
        /// Shuffles the line-up and hands out the murderer and sheriff badges.
        ///
        /// A bot can be a murderer - it hunts perfectly well once it holds a knife - but never the
        /// sheriff: judging who is guilty is the sheriff's whole job, and a bot has no way to watch
        /// a killing happen. Bots also have no loadout of their own (LoadoutService equips players
        /// only), so an armed bot is armed by hand, see ArmBotMurderers.
        /// </summary>
        private void AssignRoles()
        {
            var everyone = runningEvent.ParticipantsStillInEvent.ToList();
            var humans = everyone.Where(uid => !Bots.BotIdentity.IsBot(uid)).ToList();

            var rand = runningEvent.sapi.World.Rand;
            Shuffle(everyone, rand);
            Shuffle(humans, rand);

            int lineUp = everyone.Count;

            int murderers = murdererCount;
            if (extraMurdererPerPlayers > 0)
                murderers += lineUp / extraMurdererPerPlayers;
            murderers = System.Math.Max(0, murderers);
            int sheriffs = System.Math.Max(0, sheriffCount);

            // At least one innocent has to survive the arithmetic, or the match is over before it
            // starts - and with no murderer at all, nobody could ever win.
            if (murderers < 1 && lineUp > 0) murderers = 1;
            if (murderers > lineUp - 1) murderers = System.Math.Max(1, lineUp - 1);

            // Murderers first, from the whole line-up: a bot with a knife plays that part fine.
            var murdererUids = everyone.Take(murderers).ToList();
            foreach (var uid in murdererUids) roles[uid] = Role.Murderer;

            // Sheriffs from the humans that are left, and only as many as there is room for once a
            // murderer and one innocent are accounted for.
            int sheriffRoom = System.Math.Max(0, lineUp - murderers - 1);
            var sheriffUids = humans.Where(uid => !roles.ContainsKey(uid))
                .Take(System.Math.Min(sheriffs, sheriffRoom))
                .ToList();
            foreach (var uid in sheriffUids) roles[uid] = Role.Sheriff;

            foreach (var uid in everyone)
                if (!roles.ContainsKey(uid)) roles[uid] = Role.Innocent;

            // A bot has no kit to choose; writing a choice for one would only confuse the loadout.
            foreach (var uid in humans) runningEvent.PlayerLoadoutChoices[uid] = SetNameOf(roles[uid]);

            log.Notification("Murder Mystery: {0} in the line-up ({1} bot(s)), {2} murderer(s), {3} sheriff(s).",
                lineUp, lineUp - humans.Count, murdererUids.Count, sheriffUids.Count);
        }

        private static void Shuffle(List<string> list, Random rand)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = rand.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        /// <summary>
        /// Takes the weapon out of the hand everyone can see. The held item is rendered on the body
        /// for every other player, so a knife or a bow at the pedestal names the murderer and the
        /// sheriff before the match has begun - the loadout puts the first weapon in the main hand.
        ///
        /// Moved to a slot the player is not currently holding, and the hand is then filled with the
        /// same harmless thing everyone else got (the ration): an empty hand among full ones would
        /// be just as much of a tell. Drawing the weapon is then the murderer's own decision, and
        /// the moment it appears is a real, earned clue.
        /// </summary>
        private void ConcealWeapon(IServerPlayer player)
        {
            if (!concealWeapons) return;
            var hotbar = player?.InventoryManager?.GetOwnInventory(GlobalConstants.hotBarInvClassName);
            if (hotbar == null) return;

            // Off-hand and beyond are not part of the visible hotbar row (see LoadoutService).
            int lastSlot = System.Math.Min(9, hotbar.Count - 1);
            if (lastSlot < 1) return;

            int active = player.InventoryManager.ActiveHotbarSlotNumber;
            if (active < 0 || active > lastSlot) active = 0;

            var handSlot = hotbar[active];
            if (IsWeapon(handSlot?.Itemstack))
            {
                // From the far end: the near slots are the ones people cycle through by habit.
                for (int i = lastSlot; i >= 0; i--)
                {
                    if (i == active || hotbar[i].Itemstack != null) continue;
                    hotbar[i].Itemstack = handSlot.Itemstack;
                    handSlot.Itemstack = null;
                    hotbar[i].MarkDirty();
                    handSlot.MarkDirty();
                    break;
                }
            }

            // Same thing in every hand: the ration the loadout handed out.
            if (handSlot?.Itemstack != null) return;
            for (int i = 0; i <= lastSlot; i++)
            {
                if (i == active) continue;
                var stack = hotbar[i].Itemstack;
                if (stack == null || IsWeapon(stack)) continue;
                handSlot.Itemstack = stack;
                hotbar[i].Itemstack = null;
                handSlot.MarkDirty();
                hotbar[i].MarkDirty();
                break;
            }
        }

        /// <summary>
        /// Marks this player's weapon to survive their death and lie on the ground. The bow is the
        /// point: whoever picks it up becomes the town's next sheriff, so a detective who dies badly
        /// still leaves the town something to work with - and a suspicious item near a body.
        /// </summary>
        private void MarkLootableWeapon(IServerPlayer player)
        {
            var role = RoleOf(player.PlayerUID);
            bool lootable = role == Role.Sheriff ? sheriffDropsWeapon
                : role == Role.Murderer && murdererDropsWeapon;
            if (!lootable) return;

            foreach (var inv in player.InventoryManager.Inventories.Values)
            {
                if (inv.ClassName == "creative") continue;
                if (inv.ClassName == GlobalConstants.characterInvClassName) continue;

                for (int i = 0; i < inv.Count; i++)
                {
                    var stack = inv[i]?.Itemstack;
                    if (IsWeapon(stack)) IssuedGear.MarkDropOnDeath(stack);
                }
            }
        }

        /// <summary>Anything that reads as a weapon in someone's hand. Tool covers the knife and the
        /// bow; AttackPower catches a club or a rock handed out as a kit item.</summary>
        private static bool IsWeapon(ItemStack stack)
            => stack?.Collectible != null
               && (stack.Collectible.Tool != null || stack.Collectible.AttackPower > 1f);

        private string SetNameOf(Role role) => role switch
        {
            Role.Murderer => murdererSet,
            Role.Sheriff => sheriffSet,
            _ => innocentSet
        };

        /// <summary>Whispers each player their own role, and nobody else's.</summary>
        private void TellEveryoneTheirRole()
        {
            int murderers = roles.Count(kvp => kvp.Value == Role.Murderer);

            foreach (var player in runningEvent.OnlineParticipants)
            {
                if (!roles.TryGetValue(player.PlayerUID, out var role)) continue;
                string message = role switch
                {
                    // The murderer is told how many of them there are: alone or in company changes
                    // how they should play, and they cannot count themselves.
                    Role.Murderer => Lang.Get("canevents:mm-role-murderer", murderers),
                    Role.Sheriff => Lang.Get("canevents:mm-role-sheriff"),
                    _ => Lang.Get("canevents:mm-role-innocent")
                };
                player.SendMessage(0, message, EnumChatType.Notification);
            }
        }

        protected override void OnScoredDeath(IServerPlayer victim, KillInfo kill)
            => PunishSheriff(victim.PlayerUID, kill.HasKiller ? kill.KillerUid : null, kill.KillerName);

        /// <summary>
        /// A sheriff who shoots the wrong person dies for it. Reached from both death paths: a
        /// player's death arrives through OnScoredDeath, a bot's never does - the manager only
        /// dispatches OnParticipantDeath for players - so bot kills come in through OnAnyEntityDeath.
        /// </summary>
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

            // The sheriff pays for the mistake with their own life. Dealt as damage rather than an
            // elimination so the death runs the normal path - feed, scoreboard, spectator seat -
            // and re-enters this behavior as an ordinary death.
            var body = runningEvent.BodyOf(killerUid);
            body?.ReceiveDamage(new DamageSource
            {
                Source = EnumDamageSource.Internal,
                Type = EnumDamageType.Injury
            }, 9999f);

            EventBroadcaster.Broadcast(runningEvent,
                Lang.Get("canevents:mm-sheriff-penalty", killerName ?? NameOf(killerUid)));
        }

        /// <summary>
        /// The softer penalty: the weapon is taken away for a while and the sheriff is slowed, so a
        /// bad shot costs them the round's initiative instead of ending their match. Told privately -
        /// announcing it to the arena would name the sheriff, which the whole mode works to avoid.
        /// </summary>
        private void DisarmSheriff(string sheriffUid)
        {
            var player = runningEvent.sapi.World.PlayerByUid(sheriffUid) as IServerPlayer;
            if (player == null) return;

            var taken = TakeWeapons(player);
            if (taken.Count > 0)
            {
                // Handed back rather than replaced, so a bow that was half-used comes back as it was.
                runningEvent.sapi.Event.RegisterCallback(_ =>
                {
                    var pl = runningEvent.sapi.World.PlayerByUid(sheriffUid) as IServerPlayer;
                    if (pl?.Entity == null || !pl.Entity.Alive) return;
                    if (!runningEvent.IsActive(sheriffUid)) return;
                    foreach (var stack in taken) InventoryUtil.Give(runningEvent.sapi, pl, stack, "mm-rearm");
                    // Straight back out of the visible hand: a bow that reappears in front of
                    // everyone undoes the concealment the sheriff has been living by all match.
                    ConcealWeapon(pl);
                    EventBroadcaster.ClearSpeedMutator(pl);
                    pl.SendMessage(0, Lang.Get("canevents:mm-sheriff-rearmed"), EnumChatType.Notification);
                }, (int)(sheriffDisarmSeconds * 1000));
            }

            if (sheriffDisarmSlowness > 0f && sheriffDisarmSlowness < 1f)
                EventBroadcaster.ApplySpeedMutator(player, sheriffDisarmSlowness);

            player.SendMessage(0,
                Lang.Get("canevents:mm-sheriff-disarmed", (int)sheriffDisarmSeconds),
                EnumChatType.Notification);
        }

        /// <summary>Pulls every weapon out of a player's hands and bags, returning what was taken.</summary>
        private List<ItemStack> TakeWeapons(IServerPlayer player)
        {
            var taken = new List<ItemStack>();
            foreach (var inv in player.InventoryManager.Inventories.Values)
            {
                if (inv.ClassName == "creative") continue;
                if (inv.ClassName == GlobalConstants.characterInvClassName) continue;   // worn clothes

                for (int i = 0; i < inv.Count; i++)
                {
                    var slot = inv[i];
                    if (!IsWeapon(slot?.Itemstack)) continue;
                    taken.Add(slot.Itemstack);
                    slot.Itemstack = null;
                    slot.MarkDirty();
                }
            }
            return taken;
        }

        private static string ParsePenalty(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return PenaltyDeath;
            var v = value.Trim();
            if (v.Equals(PenaltyDisarm, StringComparison.OrdinalIgnoreCase)) return PenaltyDisarm;
            if (v.Equals(PenaltyNone, StringComparison.OrdinalIgnoreCase)
                || v.Equals("false", StringComparison.OrdinalIgnoreCase)) return PenaltyNone;
            return PenaltyDeath;   // "death", "true", and anything unreadable: the strictest reading
        }

        /// <summary>
        /// The bot half of the death handling. A bot's death never reaches OnParticipantDeath (the
        /// manager dispatches that for players only), so without this a sheriff could shoot the
        /// townsfolk bots with impunity and killing the last murderer - if it was a bot - decided
        /// nothing.
        /// </summary>
        private void OnAnyEntityDeath(Vintagestory.API.Common.Entities.Entity entity, DamageSource source)
        {
            if (entity is EntityPlayer) return;   // players come in through the scored path

            string victimUid = Bots.BotMatchLink.UidOf(entity);
            if (victimUid == null || !runningEvent.IsParticipant(victimUid)) return;

            lastDeathAt = runningEvent.ElapsedSeconds;

            string killerUid = Bots.BotMatchLink.UidOf(source?.GetCauseEntity());
            PunishSheriff(victimUid, killerUid, NameOf(killerUid));

            // ArenaBotsEB subscribes first (it comes first in the event JSON) and has already taken
            // the bot off the roster by now, so the count this reads is the one that matters.
            CheckForWinner();
        }

        protected override void AfterScoredDeath(IServerPlayer victim, KillInfo kill)
        {
            lastDeathAt = runningEvent.ElapsedSeconds;
            runningEvent.EliminatePlayer(victim.PlayerUID);
            CheckForWinner();
        }

        /// <summary>
        /// The two ways a match ends. The default "last group standing" fallback answers only the
        /// murderer's win: innocents win while several of them are still alive, which is several
        /// groups, so that side has to be resolved explicitly.
        /// </summary>
        private void CheckForWinner()
        {
            if (MatchResolved) return;
            // Before the roles are dealt everyone reads as innocent, which is "no murderers left" -
            // the tick would declare the town victorious before the match had begun.
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

            // Nobody left to play it out: with every human dead, what remains is bots chasing bots
            // for the rest of the clock while the people who are out sit in the spectator seat,
            // unable to leave a match that is still "running". Call it for the side that is ahead.
            if (!alive.Any(uid => !Bots.BotIdentity.IsBot(uid)))
                ResolveMatch(SurvivingSide(Role.Murderer), "no players left to decide it");
        }

        /// <summary>The winning side, dead members included: a sheriff who took the murderer down
        /// with them won the match for their side and should be paid for it.</summary>
        private IEnumerable<string> SurvivingSide(Role side)
            => runningEvent.ParticipantsBefore.Where(uid => side == Role.Murderer
                ? RoleOf(uid) == Role.Murderer
                : RoleOf(uid) != Role.Murderer);

        // Time-up: the murderer failed to finish the job, so the town wins. The manager has already
        // decided to finish, hence requestFinish: false.
        public override void OnBeforeFinish()
        {
            if (MatchResolved) return;
            ResolveMatch(SurvivingSide(Role.Innocent), "time up", requestFinish: false);
        }

        public override void OnParticipantJoinedGame()
        {
            // A reconnect brings a fresh entity and a fresh inventory: the damage hook would hang on
            // the dead body, and the outfit listener on an inventory nobody uses any more.
            foreach (var player in runningEvent.OnlineParticipants)
            {
                if (!innocentsCanFight) hooks.RehookIfStale(player);
                RewatchOutfit(player);
            }
            if (!innocentsCanFight) HookEveryone();
        }

        public override void OnEnd()
        {
            runningEvent.sapi.Event.OnEntityDeath -= OnAnyEntityDeath;
            hooks.UnhookAll();   // CRITICAL: a leaked hook would mute damage in the next match
            UnwatchOutfits();    // as would a listener that keeps re-dressing people afterwards

            var murderers = runningEvent.ParticipantsBefore
                .Where(uid => RoleOf(uid) == Role.Murderer)
                .Select(NameOf)
                .ToList();
            if (murderers.Count > 0)
                EventBroadcaster.Broadcast(runningEvent,
                    Lang.Get("canevents:mm-reveal", string.Join(", ", murderers)));

            var winners = runningEvent.GetWinners();
            bool murderersWon = winners.Count > 0 && winners.Any(uid => RoleOf(uid) == Role.Murderer);
            EventBroadcaster.Broadcast(runningEvent, Lang.Get(murderersWon
                ? "canevents:mm-win-murderers"
                : "canevents:mm-win-innocents"));
        }

        private Role RoleOf(string uid)
            => uid != null && roles.TryGetValue(uid, out var r) ? r : Role.Innocent;

        private string NameOf(string uid)
            => Bots.BotIdentity.IsBot(uid)
                ? Bots.BotIdentity.NameOfUid(uid)
                : (runningEvent.sapi.World.PlayerByUid(uid)?.PlayerName ?? uid);

        private void HookDamage(string victimUid)
        {
            hooks.Hook(victimUid, runningEvent.BodyOf(victimUid), (damage, source) =>
            {
                if (damage <= 0f) return damage;
                // GetCauseEntity() resolves the archer behind an arrow, so this covers bows too -
                // not that an innocent has one.
                if (source?.GetCauseEntity() is not EntityPlayer attacker) return damage;
                string attackerUid = attacker.PlayerUID;
                if (attackerUid == victimUid) return damage;
                if (!runningEvent.IsParticipant(attackerUid)) return damage;
                return RoleOf(attackerUid) == Role.Innocent ? 0f : damage;
            });
        }
    }
}
