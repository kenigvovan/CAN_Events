using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.commands;
using canevents.src.EventManagers;
using canevents.src.EventManagers.eventBehavior;
using canevents.src.EventManagers.eventBehavior.abstracts;
using canevents.src.EventManagers.events;
using canevents.src.EventPlacesManagers;
using canevents.src.EventPlacesManagers.Places;
using canevents.src.HarmonyPatches;
using canparty;
using canevents.src.QueueManagers;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.API.Util;

namespace canevents.src
{
    public partial class canevents
    {
        public override void StartServerSide(ICoreServerAPI api)
        {
            LoadConfig(api);

            // Logged so bug-report logs show which switches were on.
            api.Logger.Notification(
                "[canevents] switches: pinArenaChunks={0} arenaOutlineParticles={1} ({2}s) "
                + "diagnosticsLogging={3} inventoryAudit={4}",
                config.pinArenaChunks, config.arenaOutlineParticles,
                config.arenaOutlineIntervalSeconds, config.diagnosticsLogging, config.inventoryAudit);
            // Before anything can take an inventory.
            EventManagers.InventoryAuditLog.Configure(api, config.inventoryAudit, config.inventoryAuditMaxEntries);

            // Before commands and network handlers: both gate on it.
            CanEventsPrivileges.RegisterAll(api);
            // canparty is a hard dependency.
            partyManager = api.ModLoader.GetModSystem<canparty.canpartyModSystem>()?.Manager;
            if (partyManager == null)
            {
                api.Logger.Error("[canevents] canparty mod is required but its PartyManager is unavailable. Party queues will not work.");
            }



            RegisterEventBehavior(typeof(SavePlayerAttributes), BehaviorNames.SavePlayerAttributes);
            RegisterEventBehavior(typeof(FullPartyRequired), BehaviorNames.FullPartyRequired);
            RegisterEventBehavior(typeof(ByPartsPartyCollected), BehaviorNames.ByPartsPartyCollected);
            RegisterEventBehavior(typeof(TPThereAndBack), BehaviorNames.TPThereAndBack);
            RegisterEventBehavior(typeof(ArenaFightEB), BehaviorNames.ArenaFight);
            RegisterEventBehavior(typeof(NoFriendlyFireEB), BehaviorNames.NoFriendlyFire);
            RegisterEventBehavior(typeof(SpleefEB), BehaviorNames.Spleef);
            RegisterEventBehavior(typeof(ShrinkingSpleefEB), BehaviorNames.ShrinkingSpleef);
            RegisterEventBehavior(typeof(ParkourEB), BehaviorNames.Parkour);
            RegisterEventBehavior(typeof(DeathmatchEB), BehaviorNames.Deathmatch);
            RegisterEventBehavior(typeof(KingOfTheHillEB), BehaviorNames.KingOfTheHill);
            RegisterEventBehavior(typeof(HungerGamesEB), BehaviorNames.HungerGames);
            RegisterEventBehavior(typeof(JuggernautEB), BehaviorNames.Juggernaut);
            RegisterEventBehavior(typeof(CaptureTheFlagEB), BehaviorNames.CaptureTheFlag);
            RegisterEventBehavior(typeof(MurderMysteryEB), BehaviorNames.MurderMystery);
            RegisterEventBehavior(typeof(ArenaBotsEB), BehaviorNames.ArenaBots);
            RegisterEventBehavior(typeof(OpenDeathmatchEB), BehaviorNames.OpenDeathmatch);
            RegisterEventBehavior(typeof(WaitingRoomEB), BehaviorNames.WaitingRoom);
            RegisterEventBehavior(typeof(BattleRoundsEB), BehaviorNames.BattleRounds);
            RegisterEventBehavior(typeof(ArenaProtectionEB), BehaviorNames.ArenaProtection);
            RegisterEventBehavior(typeof(TeamMarkersEB), BehaviorNames.TeamMarkers);
            RegisterEventBehavior(typeof(MatchShopEB), BehaviorNames.MatchShop);
            Events = new List<CANEvent>();
            foreach (var def in eventDefinitions)
            {
                if (!def.Type.Enabled)
                {
                    api.World.Logger.Notification("Event '{0}' is disabled in config, skipping.", def.Type.Code);
                    continue;
                }
                var built = BuildEvent(api, def.Type, null);
                if (built == null) continue;
                Events.Add(built);
                eventJsonByCode[built.Code] = def.Json;
            }

            this.CANEventManager = new EventManagers.CANEventManager(partyManager, api, Events.ToHashSet());
            // Shared by /canep and the event wand.
            PlaceTargeting = new PlaceTargeting(this.CANEventManager.eventPlacesManager,
                this.CANEventManager.SelectedPlaceByPlayer);

            // Removes bots whose match is gone (crash, chunk unloaded mid-match).
            botCleanup = new Bots.BotCleanup(api, this.CANEventManager);

            // Enforces config.issuedGearDrop.
            issuedGearWatcher = new EventManagers.IssuedGearDropWatcher(api, this.CANEventManager);

            ServerNetworkHandler = new Network.ServerNetworkHandler(api, this.CANEventManager);
            var networkHandler = ServerNetworkHandler;
            networkHandler.MainDialogHotkey = config.mainDialogHotkey;
            networkHandler.DiagnosticsLogging = config.diagnosticsLogging;
            networkHandler.WirePartyManager(partyManager);

            var matchHistory    = new Scoring.MatchHistory(api);
            var leaderboard     = new Scoring.LeaderboardData(api);
            var spectatorMgr    = new SpectatorManager(api);
            networkHandler.MatchHistory = matchHistory;
            this.CANEventManager.MatchHistory       = matchHistory;
            this.CANEventManager.HeadToHead         = new Scoring.HeadToHeadStore(api);
            this.CANEventManager.RewardDistributor  = new Scoring.RewardDistributor(api, config);
            this.CANEventManager.ArenaTemplates      = arenaTemplates;
            var savedBlocksPath = ModPaths.SavedBlocks;
            this.CANEventManager.SchematicPlacer = new EventPlacesManagers.SchematicPlacer(api, savedBlocksPath);
            // Restore terrain under on-demand arenas left by an unclean shutdown; persistent ones are kept.
            var placerForSweep = this.CANEventManager.SchematicPlacer;
            var placesForSweep = this.CANEventManager.eventPlacesManager;
            // Blocks matches wrote into arenas (loot chests, spleef platform/cage, flag posts).
            EventManagers.eventBehavior.Shared.ArenaBlockJournal.Configure(api, savedBlocksPath,
                placeId => placesForSweep.FindById(placeId)?.Occupied == true);
            api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, () =>
            {
                // Journal first: the schematic sweep waits for it per arena (RunAfterRestore).
                EventManagers.eventBehavior.Shared.ArenaBlockJournal.RestoreAllOrphaned();
                var keep = new System.Collections.Generic.HashSet<long>(
                    placesForSweep.EventPlaces
                        .Where(p => !p.SchematicOnDemand)
                        .Select(p => p.Id));
                placerForSweep.RestoreAllOrphaned(keep);
            });
            this.CANEventManager.TemplateEditor = new EventPlacesManagers.TemplateEditor(
                arenaTemplates,
                this.CANEventManager.eventPlacesManager,
                this.CANEventManager.SchematicPlacer,
                this.CANEventManager.placeConfig);

            this.CANEventManager.MatchServices = new MatchServices
            {
                NetworkHandler = networkHandler,
                Leaderboard    = leaderboard,
                LoadoutManager = this.CANEventManager.LoadoutManager,
                OutfitManager  = this.CANEventManager.Store(EventManagers.ItemSetKinds.Outfit),
                Spectator      = spectatorMgr,
                Config         = config,
                SeatOutOfPlay  = this.CANEventManager.SeatOutOfPlay
            };
            this.CANEventManager.Start();

            this.eventScheduler = new EventScheduler(api, config, this.CANEventManager);

            // Signup windows hold the queue open; hooked in via delegates.
            this.signupWindows = new EventManagers.SignupWindows(api, this.CANEventManager);
            this.CANEventManager.QueueHold = this.signupWindows.IsHolding;
            this.eventScheduler.AnnounceHandler =
                code => this.signupWindows.Open(code, config.signupWindowMinutes, out _, auto: true);

            // After the scheduler, which tournaments pause while a bracket runs.
            tournamentStore = new Tournaments.TournamentStore(api);
            tournamentManager = new Tournaments.TournamentManager(
                api, this.CANEventManager, tournamentStore, this.eventScheduler);
            this.CANEventManager.Tournaments = tournamentManager;
            this.CANEventManager.placeConfig.PlaceDeleted = id =>
            {
                var lines = tournamentManager.RemovePlaceFromWhitelists(id);
                int zones = JoinPads?.RemoveForPlace(id) ?? 0;
                if (zones > 0) lines.Add(Vintagestory.API.Config.Lang.Get("canevents:pad-removed-with-place", zones));
                return lines;
            };
            tournamentManager.Start();
            api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, () => tournamentManager.RecoverAfterRestart());

            // Static state would survive a world reload in the same process.
            api.Event.ServerRunPhase(EnumServerRunPhase.Shutdown,
                () =>
                {
                    EventPlacesManagers.ArenaChunkPin.Reset();
                    EventTeleport.Reset();
                    EventManagers.eventBehavior.Shared.CombatTag.Reset();
                    // Audit writes are batched.
                    EventManagers.InventoryAuditLog.Flush();
                });

            // Before commands, which reference it.
            Duels = new Duels.DuelManager(api, this.CANEventManager, config);
            Duels.Start();
            networkHandler.Duels = Duels;

            JoinPads = new EventManagers.JoinPads(api, this.CANEventManager);
            JoinPads.Start();

            ArenaProtection = new EventManagers.ArenaProtection(api, this.CANEventManager);
            ArenaProtection.Start();

            CommandsRegister.RegisterCommands(api);

            RespawnPatch.Mod = this;
            harmony = new Harmony(HarmonyId);
            RespawnPatch.Apply(harmony);
            // Server owns the collision box.
            HarmonyPatches.SpectatorBodyMetricsPatch.Apply(harmony);
            // Other mods' on-death handlers that don't know about matches.
            HarmonyPatches.MatchDeathExternalsPatch.Mod = this;
            HarmonyPatches.MatchDeathExternalsPatch.Apply(harmony, api);
            // Locks mod stash slots (e.g. trash slot) during a match.
            HarmonyPatches.PersonalStashPatch.Apply(harmony, api);

            _sapi = api;
            _onDeath      = (player, source) => this.CANEventManager.OnDeath(player, source);
            // One global hook for bot deaths.
            _onEntityDeath = (entity, source) => this.CANEventManager.OnEntityDeath(entity, source);
            _onRespawn    = player => this.CANEventManager.OnRespawn(player);
            _onDisconnect = player => this.CANEventManager.OnDisconnect(player);
            _onReconnect  = player =>
            {
                // Old builds could put armor into slots Combat Overhaul disables, crashing vanilla damage.
                Compat.CharacterInventoryCompat.ClearUnusableSlots(player, api.World.Logger);
                EventManagers.eventBehavior.Shared.CombatTag.Track(api, player);
                this.CANEventManager.OnReconnect(player);
            };
            api.Event.PlayerDeath       += _onDeath;
            api.Event.OnEntityDeath     += _onEntityDeath;
            api.Event.PlayerRespawn     += _onRespawn;
            api.Event.PlayerDisconnect  += _onDisconnect;
            api.Event.PlayerNowPlaying  += _onReconnect;

        }

        private void LoadConfig(ICoreAPI api)
        {
            config = api.LoadModConfig<Config>(this.Mod.Info.ModID + ".json");

            // Write back so new keys show up in old files.
            config ??= new Config();
            AdminStateStore.SaveConfig(api, config);
        }
        public bool RegisterEventBehavior(Type behavior, string name)
        {
            if (CANEventBehaviors.TryAdd(name, behavior))
            {
                return true;
            }
            return false;
        }
    }
}
