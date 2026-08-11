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
    public class canevents : ModSystem
    {
        public PartyManager partyManager;
        public EventScheduler eventScheduler;
        public EventManagers.SignupWindows signupWindows;

        public Config config;
        public Dictionary<string, Type> CANEventBehaviors = new Dictionary<string, Type>();
        List<CANEventType> eventTypes = new List<CANEventType>();
        List<EventPlacesManagers.ArenaTemplate> arenaTemplates = new List<EventPlacesManagers.ArenaTemplate>();
        List<CANEvent> Events;
        public CANEventManager CANEventManager { get; set; }
        private Harmony harmony;
        private Bots.BotCleanup botCleanup;
        private Tournaments.TournamentStore tournamentStore;
        private Tournaments.TournamentManager tournamentManager;
        private ICoreServerAPI _sapi;
        private PlayerDeathDelegate _onDeath;
        private PlayerDelegate _onRespawn;
        private PlayerDelegate _onDisconnect;
        private PlayerDelegate _onReconnect;

        // Run after canparty (default 0.1) so its PartyManager exists when we grab it.
        public override double ExecuteOrder() => 0.3;

        public override void Start(ICoreAPI api)
        {
            api.RegisterBlockEntityClass("SpleefMelt", typeof(BlockEntities.BlockEntitySpleefMelt));
            api.RegisterBlockEntityBehaviorClass("SpleefMelt", typeof(BlockEntities.BEBehaviorSpleefMelt));
            api.RegisterBlockEntityClass("ParkourCheckpoint", typeof(BlockEntities.BlockEntityParkourCheckpoint));
            api.RegisterBlockEntityBehaviorClass("ParkourCheckpoint", typeof(BlockEntities.BEBehaviorParkourCheckpoint));
            api.RegisterBlockBehaviorClass("CtfFlag", typeof(BlockBehaviors.BlockBehaviorCtfFlag));

            // Event bots: entity class and AI tasks must be registered on both sides, or the client
            // logs a warning per bot.
            api.RegisterEntity("ArenaBot", typeof(Bots.EntityArenaBot));
            Vintagestory.GameContent.AiTaskRegistry.Register<Bots.AiTaskArenaBotSeek>("caneventsbotseek");
            Vintagestory.GameContent.AiTaskRegistry.Register<Bots.AiTaskArenaBotMelee>("caneventsbotmelee");
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            LoadConfig(api);
            // Party system lives in the canparty mod (hard dependency); we only consume it.
            partyManager = api.ModLoader.GetModSystem<canparty.canpartyModSystem>()?.Manager;
            if (partyManager == null)
            {
                api.Logger.Error("[canevents] canparty mod is required but its PartyManager is unavailable. Party queues will not work.");
            }



            RegisterEventBehavior(typeof(SavePlayerAttributes), "SavePlayerAttributes");
            RegisterEventBehavior(typeof(FullPartyRequired), "FullPartyRequired");
            RegisterEventBehavior(typeof(ByPartsPartyCollected), "ByPartsPartyCollected");
            RegisterEventBehavior(typeof(TPThereAndBack), "TPThereAndBack");
            RegisterEventBehavior(typeof(ArenaFightEB), "ArenaFight");
            RegisterEventBehavior(typeof(NoFriendlyFireEB), "NoFriendlyFire");
            RegisterEventBehavior(typeof(SpleefEB), "Spleef");
            RegisterEventBehavior(typeof(ShrinkingSpleefEB), "ShrinkingSpleef");
            RegisterEventBehavior(typeof(ParkourEB), "Parkour");
            RegisterEventBehavior(typeof(DeathmatchEB), "Deathmatch");
            RegisterEventBehavior(typeof(KingOfTheHillEB), "KingOfTheHill");
            RegisterEventBehavior(typeof(HungerGamesEB), "HungerGames");
            RegisterEventBehavior(typeof(JuggernautEB), "Juggernaut");
            RegisterEventBehavior(typeof(CaptureTheFlagEB), "CaptureTheFlag");
            RegisterEventBehavior(typeof(ArenaBotsEB), "ArenaBots");
            Events = new List<CANEvent>();
            foreach (var it in eventTypes)
            {
                if (!it.Enabled)
                {
                    api.World.Logger.Notification("Event '{0}' is disabled in config, skipping.", it.Code);
                    continue;
                }
                // EventType is only a category label; the behavior list is the real contract.
                var tmpEvent = new CANEvent();
                foreach (var behaior in it.Behaviors)
                {
                    if (CANEventBehaviors.TryGetValue(behaior.Name, out var behaviorType))
                    {
                        var behaviorInstance = Activator.CreateInstance(behaviorType, new object[] { behaior.Name, behaior.Attributes, null }) as CANEventBehavior;
                        behaviorInstance.Initialize();
                        if (behaviorInstance is PartyAssemblingEB)
                        {
                            tmpEvent.AssemblingEB = behaviorInstance as PartyAssemblingEB;
                        }
                        tmpEvent.Behaviors.Add(behaviorInstance);
                    }
                }
                foreach (var en in it.EventPlaceTypes)
                {
                    var tag = PlaceTags.Normalize(en);
                    if (!string.IsNullOrEmpty(tag)) tmpEvent.EventTypes.Add(tag);
                }
                tmpEvent.Code = it.Code;
                tmpEvent.Category = string.IsNullOrWhiteSpace(it.Category) ? it.EventType : it.Category;
                tmpEvent.Reward = it.Reward;
                tmpEvent.EntryFee = it.EntryFee;
                tmpEvent.Rated = it.Rated;
                Events.Add(tmpEvent);
            }

            this.CANEventManager = new EventManagers.CANEventManager(partyManager, api, Events.ToHashSet());

            // Sweeps bot bodies whose match is gone (crash mid-match, chunk unloaded while it ran).
            botCleanup = new Bots.BotCleanup(api, this.CANEventManager);

            // Initialize network
            ServerNetworkHandler = new Network.ServerNetworkHandler(api, this.CANEventManager);
            var networkHandler = ServerNetworkHandler;
            networkHandler.MainDialogHotkey = config.mainDialogHotkey;
            networkHandler.WirePartyManager(partyManager);

            // Initialize rewards, leaderboard, spectator
            var matchHistory    = new Scoring.MatchHistory(api);
            var leaderboard     = new Scoring.LeaderboardData(api);
            var spectatorMgr    = new SpectatorManager(api);
            networkHandler.MatchHistory = matchHistory;
            this.CANEventManager.MatchHistory       = matchHistory;
            this.CANEventManager.RewardDistributor  = new Scoring.RewardDistributor(api, config);
            this.CANEventManager.ArenaTemplates      = arenaTemplates;
            var savedBlocksPath = System.IO.Path.Combine(Vintagestory.API.Config.GamePaths.ModConfig, "canevents", "savedBlocks");
            this.CANEventManager.SchematicPlacer = new EventPlacesManagers.SchematicPlacer(api, savedBlocksPath);
            // Sweep schematic arenas left placed by an unclean shutdown and restore the terrain.
            // Persistent "Place Here" arenas keep their rollback file on purpose and are kept;
            // only on-demand arenas (and files with no registered place) are true orphans.
            var placerForSweep = this.CANEventManager.SchematicPlacer;
            var placesForSweep = this.CANEventManager.eventPlacesManager;
            api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, () =>
            {
                var keep = new System.Collections.Generic.HashSet<long>(
                    placesForSweep.EventPlaces
                        .Where(p => !p.SchematicOnDemand)
                        .Select(p => p.Id));
                placerForSweep.RestoreAllOrphaned(keep);
            });
            // HG loot chests use the same crash-safe rollback (hgloot_*.json); sweep orphans too.
            EventManagers.eventBehavior.Shared.HungerGamesLootStore.Configure(api, savedBlocksPath);
            api.Event.ServerRunPhase(EnumServerRunPhase.RunGame,
                () => EventManagers.eventBehavior.Shared.HungerGamesLootStore.RestoreAllOrphaned());
            this.CANEventManager.TemplateEditor = new EventPlacesManagers.TemplateEditor(
                arenaTemplates,
                this.CANEventManager.eventPlacesManager,
                this.CANEventManager.SchematicPlacer);

            // MatchServices is the single source of truth for the shared services.
            this.CANEventManager.MatchServices = new MatchServices
            {
                NetworkHandler = networkHandler,
                Leaderboard    = leaderboard,
                LoadoutManager = this.CANEventManager.LoadoutManager,
                Spectator      = spectatorMgr,
                Config         = config
            };
            this.CANEventManager.Start();

            // Initialize scheduler
            this.eventScheduler = new EventScheduler(api, config, this.CANEventManager);

            // Registration windows hold the queue open on purpose, so they need both the manager whose
            // assembly they pause and the scheduler that starts the match when the time is up. The two
            // wires back out are delegates, so neither the match driver nor the scheduler has to know
            // this feature exists.
            this.signupWindows = new EventManagers.SignupWindows(api, this.CANEventManager, this.eventScheduler);
            this.CANEventManager.QueueHold = this.signupWindows.IsHolding;
            this.eventScheduler.AnnounceHandler =
                code => this.signupWindows.Open(code, config.signupWindowMinutes, out _, auto: true);

            // Tournaments drive the match machinery externally; wired after the scheduler so they can
            // pause it while a bracket runs. No RunningEvent survives a restart, so in-flight matches
            // are reset to pending and replayed.
            tournamentStore = new Tournaments.TournamentStore(api);
            tournamentManager = new Tournaments.TournamentManager(
                api, this.CANEventManager, tournamentStore, this.eventScheduler);
            this.CANEventManager.Tournaments = tournamentManager;
            tournamentManager.Start();
            api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, () => tournamentManager.RecoverAfterRestart());

            // Arena chunk pins are static and would survive a world reload inside the same process,
            // leaving arenas that no longer exist counted as held (see ArenaChunkPin.Reset).
            api.Event.ServerRunPhase(EnumServerRunPhase.Shutdown,
                () => EventPlacesManagers.ArenaChunkPin.Reset());

            CommandsRegister.RegisterCommands(api);

            RespawnPatch.Mod = this;
            harmony = new Harmony("canevents.respawn");
            RespawnPatch.Apply(harmony);

            _sapi = api;
            _onDeath      = (player, source) => this.CANEventManager.OnDeath(player, source);
            _onRespawn    = player => this.CANEventManager.OnRespawn(player);
            _onDisconnect = player => this.CANEventManager.OnDisconnect(player);
            _onReconnect  = player => this.CANEventManager.OnReconnect(player);
            api.Event.PlayerDeath       += _onDeath;
            api.Event.PlayerRespawn     += _onRespawn;
            api.Event.PlayerDisconnect  += _onDisconnect;
            api.Event.PlayerNowPlaying  += _onReconnect;

        }

        public override void AssetsLoaded(ICoreAPI api)
        {
            base.AssetsLoaded(api);
            if (api is ICoreServerAPI serverApi)
            {
                // Event definitions live in ModConfig/canevents/events/ so admins can edit them without
                // rebuilding; the shipped assets are only the seed source for that folder.
                var userEventsDir = System.IO.Path.Combine(Vintagestory.API.Config.GamePaths.ModConfig, "canevents", "events");
                try
                {
                    if (!System.IO.Directory.Exists(userEventsDir)) System.IO.Directory.CreateDirectory(userEventsDir);
                    // Seed only files the folder lacks (by name); never overwrite - the folder copy wins.
                    foreach (var asset in api.Assets.GetMany("config/events/", "canevents"))
                    {
                        if (!asset.Location.Path.EndsWithOrdinal(".json")) continue;
                        var fname = System.IO.Path.GetFileName(asset.Location.Path);
                        var target = System.IO.Path.Combine(userEventsDir, fname);
                        if (System.IO.File.Exists(target)) continue;
                        System.IO.File.WriteAllBytes(target, asset.Data);
                        api.World.Logger.Notification("Seeded event definition: {0}", fname);
                    }
                }
                catch (Exception exSeedEvents)
                {
                    api.World.Logger.Warning("Failed to seed default event definitions: {0}", exSeedEvents.Message);
                }

                // The ModConfig folder is the authoritative copy.
                if (System.IO.Directory.Exists(userEventsDir))
                {
                    foreach (var file in System.IO.Directory.GetFiles(userEventsDir, "*.json"))
                    {
                        try
                        {
                            var eventType = Newtonsoft.Json.JsonConvert.DeserializeObject<CANEventType>(System.IO.File.ReadAllText(file));
                            if (eventType == null || string.IsNullOrEmpty(eventType.Code))
                            {
                                api.World.Logger.Warning("Event definition {0} has no code, skipped.", file);
                                continue;
                            }
                            eventType.InitType();
                            eventTypes.Add(eventType);
                        }
                        catch (Exception e)
                        {
                            api.World.Logger.Error("Event definition {0} could not be loaded: {1}", file, e.Message);
                        }
                    }
                }

                // Seed default schematics into ModConfig/canevents/schematics/ (only if missing).
                try
                {
                    var schematicsDir = System.IO.Path.Combine(Vintagestory.API.Config.GamePaths.ModConfig, "canevents", "schematics");
                    if (!System.IO.Directory.Exists(schematicsDir)) System.IO.Directory.CreateDirectory(schematicsDir);
                    foreach (var asset in api.Assets.GetMany("config/defaultschematics/", "canevents"))
                    {
                        if (!asset.Location.Path.EndsWithOrdinal(".json")) continue;
                        var fname = System.IO.Path.GetFileName(asset.Location.Path);
                        var target = System.IO.Path.Combine(schematicsDir, fname);
                        if (System.IO.File.Exists(target)) continue;
                        System.IO.File.WriteAllBytes(target, asset.Data);
                        api.World.Logger.Notification("Seeded default schematic: {0}", fname);
                    }
                }
                catch (Exception exSeed)
                {
                    api.World.Logger.Warning("Failed to seed default schematics: {0}", exSeed.Message);
                }

                // Load arena templates from mod assets
                foreach (KeyValuePair<AssetLocation, JObject> entry in api.Assets.GetMany<JObject>(serverApi.Server.Logger, "config/arenas/", "canevents"))
                {
                    if (entry.Key.Path.EndsWithOrdinal(".json"))
                    {
                        try
                        {
                            var template = entry.Value.ToObject<ArenaTemplate>();
                            arenaTemplates.Add(template);
                            api.World.Logger.Notification("Loaded arena template: {0}", template.Code);
                        }
                        catch (Exception e)
                        {
                            api.World.Logger.Error("Arena template {0} could not be loaded: {1}", entry.Key, e.Message);
                        }
                    }
                }

                // Load / override arena templates from ModConfig (user-editable)
                var userArenasDir = System.IO.Path.Combine(Vintagestory.API.Config.GamePaths.ModConfig, "canevents", "arenas");
                if (!System.IO.Directory.Exists(userArenasDir))
                {
                    System.IO.Directory.CreateDirectory(userArenasDir);
                }
                foreach (var file in System.IO.Directory.EnumerateFiles(userArenasDir, "*.json"))
                {
                    try
                    {
                        var json = System.IO.File.ReadAllText(file);
                        var template = Newtonsoft.Json.JsonConvert.DeserializeObject<ArenaTemplate>(json);
                        if (template == null || string.IsNullOrEmpty(template.Code))
                        {
                            api.World.Logger.Warning("Arena template {0} has no code, skipped.", file);
                            continue;
                        }
                        var existing = arenaTemplates.FindIndex(t => t.Code == template.Code);
                        if (existing >= 0)
                        {
                            arenaTemplates[existing] = template;
                            api.World.Logger.Notification("Overrode arena template from ModConfig: {0}", template.Code);
                        }
                        else
                        {
                            arenaTemplates.Add(template);
                            api.World.Logger.Notification("Loaded arena template from ModConfig: {0}", template.Code);
                        }
                    }
                    catch (Exception e)
                    {
                        api.World.Logger.Error("Arena template {0} could not be loaded: {1}", file, e.Message);
                    }
                }
            }
        }
        public Network.ServerNetworkHandler ServerNetworkHandler { get; private set; }
        public Network.ClientNetworkHandler ClientNetworkHandler { get; private set; }
        private Gui.Native.EventHudGui eventHudGui;
        private Gui.Native.HudLayoutEditorGui hudLayoutEditor;
        private Gui.HudLayout hudLayout;
        private Gui.Native.EventBrowserGui eventBrowserGui;
        private Gui.Native.LoadoutPickGui loadoutPickGui;
        private Gui.Native.TournamentReadyGui tournamentReadyGui;
        private Action<Network.TournamentReadyRequestMsg> _onTournamentReady;
        private Action<Network.TournamentReadyClosedMsg> _onTournamentReadyClosed;
        private Gui.Native.EventInviteHud eventInviteHud;
        private Gui.AdminPanelDialog adminPanel;
        private Gui.CtfFlagRenderer ctfFlagRenderer;
        private Gui.HgZoneDomeRenderer hgZoneDomeRenderer;
        private ICoreClientAPI clientApi;   // kept only to unregister the renderer on dispose

        public override void StartClientSide(ICoreClientAPI api)
        {
            // The hotkey starts at a hardcoded default; the admin value arrives via ClientConfigSyncMsg
            // and is applied unless the player rebound it.
            ClientNetworkHandler = new Network.ClientNetworkHandler(api);

            // Player-facing GUI uses the game's own dialogs, not ImGui: VSImGui is a client-side mod and
            // without it nothing drew. Only the admin panel stays on ImGui.
            hudLayout = Gui.HudLayout.Load(api);
            eventHudGui = new Gui.Native.EventHudGui(api, ClientNetworkHandler, hudLayout);
            hudLayoutEditor = new Gui.Native.HudLayoutEditorGui(api, hudLayout, () =>
            {
                eventHudGui.OnLayoutChanged();
                eventInviteHud?.OnLayoutChanged();
            });
            loadoutPickGui = new Gui.Native.LoadoutPickGui(api, ClientNetworkHandler);
            eventBrowserGui = new Gui.Native.EventBrowserGui(api, ClientNetworkHandler)
            {
                LoadoutPick = loadoutPickGui,
                OnEditHudLayout = () => hudLayoutEditor.TryOpen()
            };
            tournamentReadyGui = new Gui.Native.TournamentReadyGui(api, ClientNetworkHandler);
            // The signup invite is a HUD toast, not a dialog: it must stay readable for the whole
            // window without holding the mouse, and it subscribes to its own packets.
            eventInviteHud = new Gui.Native.EventInviteHud(api, ClientNetworkHandler, hudLayout)
            {
                LoadoutPick = loadoutPickGui
            };
            // Cancelling a queue-time class pick returns to the browser it was opened from.
            loadoutPickGui.OnQueuePickCancelled = () => eventBrowserGui.TryOpen();
            api.Gui.RegisterDialog(eventHudGui, eventBrowserGui, loadoutPickGui, hudLayoutEditor,
                tournamentReadyGui, eventInviteHud);

            // Two keys answer the registration panel, so both joining and changing your mind cost no
            // clicks and no open windows. Ordinary hotkeys: they appear in Settings -> Controls and
            // each player can rebind them; the panel prints whatever binding is in force.
            api.Input.RegisterHotKey(Gui.Native.EventInviteHud.JoinHotkeyCode,
                Vintagestory.API.Config.Lang.Get("canevents:hotkey-join"), GlKeys.G,
                HotkeyType.GUIOrOtherControls);
            api.Input.SetHotKeyHandler(Gui.Native.EventInviteHud.JoinHotkeyCode,
                _ => eventInviteHud.TryToggleQueue());

            // Shift+G by default: the same finger, and a modifier makes an accidental dismissal of a
            // panel you meant to answer far less likely.
            api.Input.RegisterHotKey(Gui.Native.EventInviteHud.DismissHotkeyCode,
                Vintagestory.API.Config.Lang.Get("canevents:hotkey-dismiss"), GlKeys.G,
                HotkeyType.GUIOrOtherControls, shiftPressed: true);
            api.Input.SetHotKeyHandler(Gui.Native.EventInviteHud.DismissHotkeyCode,
                _ => eventInviteHud.TryDismissCurrent());

            // The server can raise the class pick on its own (party leader queued the group), so it
            // must work with the browser closed.
            ClientNetworkHandler.OnLoadoutPickRequested += loadoutPickGui.OpenForMatchStart;

            // Same idea for the tournament ready check: it must find the player wherever they are.
            _onTournamentReady = msg => tournamentReadyGui.OpenFor(msg);
            _onTournamentReadyClosed = msg => tournamentReadyGui.CloseFor(msg.TournamentId, msg.MatchId);
            ClientNetworkHandler.OnTournamentReadyRequested += _onTournamentReady;
            ClientNetworkHandler.OnTournamentReadyClosed += _onTournamentReadyClosed;

            adminPanel = new Gui.AdminPanelDialog(api, ClientNetworkHandler);

            // Same editor from chat, for players who closed the browser and want it back.
            api.ChatCommands.Create("canhud")
                .WithDescription(Vintagestory.API.Config.Lang.Get("canevents:hud-edit-open"))
                .HandleWith(_ =>
                {
                    hudLayoutEditor.TryOpen();
                    return TextCommandResult.Success(Vintagestory.API.Config.Lang.Get("canevents:hud-edit-hint"));
                })
                .BeginSubCommand("reset")
                    .WithDescription(Vintagestory.API.Config.Lang.Get("canevents:hud-edit-reset"))
                    .HandleWith(_ =>
                    {
                        hudLayout.ResetAll();
                        hudLayout.Save(api);
                        eventHudGui.OnLayoutChanged();
                        eventInviteHud?.OnLayoutChanged();
                        return TextCommandResult.Success(Vintagestory.API.Config.Lang.Get("canevents:hud-edit-reset-done"));
                    })
                .EndSubCommand()
                // How much of the registration toast this player wants. Off is allowed on purpose:
                // the countdown also lives on the event's card in the browser, so turning the panel
                // off loses nothing but the panel.
                .BeginSubCommand("invite")
                    .WithDescription(Vintagestory.API.Config.Lang.Get("canevents:hud-invite-mode-desc"))
                    .WithArgs(api.ChatCommands.Parsers.OptionalWord("mode"))
                    .HandleWith(args =>
                    {
                        string mode = (args.Parsers[0].GetValue() as string)?.ToLowerInvariant();
                        switch (mode)
                        {
                            case "full": hudLayout.InviteMode = Gui.HudLayout.InviteDisplay.Full; break;
                            case "mini": hudLayout.InviteMode = Gui.HudLayout.InviteDisplay.Mini; break;
                            case "off":  hudLayout.InviteMode = Gui.HudLayout.InviteDisplay.Off; break;
                            case null:
                            case "":
                                // No argument: say what it is set to rather than guess at a change.
                                return TextCommandResult.Success(Vintagestory.API.Config.Lang.Get(
                                    "canevents:hud-invite-mode-current", hudLayout.InviteMode.ToString().ToLowerInvariant()));
                            default:
                                return TextCommandResult.Error(Vintagestory.API.Config.Lang.Get("canevents:hud-invite-mode-usage"));
                        }

                        hudLayout.Save(api);
                        eventInviteHud?.OnLayoutChanged();
                        return TextCommandResult.Success(Vintagestory.API.Config.Lang.Get(
                            "canevents:hud-invite-mode-set", mode));
                    })
                .EndSubCommand();

            // In-world flag on CTF carriers. Session-long: there is no client signal for "CTF began",
            // and it draws nothing while no one carries.
            clientApi = api;
            ctfFlagRenderer = new Gui.CtfFlagRenderer(api, ClientNetworkHandler);
            api.Event.RegisterRenderer(ctfFlagRenderer, EnumRenderStage.Opaque, "canevents-ctfflag");

            // HG storm dome, also session-long. OIT stage, not Opaque: its shader writes the OIT buffers.
            hgZoneDomeRenderer = new Gui.HgZoneDomeRenderer(api, ClientNetworkHandler);
            api.Event.RegisterRenderer(hgZoneDomeRenderer, EnumRenderStage.OIT, "canevents-hgzonedome");
        }
        private void LoadConfig(ICoreAPI api)
        {
            config = api.LoadModConfig<Config>(this.Mod.Info.ModID + ".json");

            // Always written back so keys added in a new mod version appear in an existing file.
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

        public override void Dispose()
        {
            if (_sapi != null)
            {
                _sapi.Event.PlayerDeath      -= _onDeath;
                _sapi.Event.PlayerRespawn    -= _onRespawn;
                _sapi.Event.PlayerDisconnect -= _onDisconnect;
                _sapi.Event.PlayerNowPlaying -= _onReconnect;
            }
            botCleanup?.Dispose();
            // Before CANEventManager.Dispose: it unsubscribes from the manager's match events.
            tournamentManager?.Dispose();
            ServerNetworkHandler?.Dispose();
            // Before CANEventManager.Dispose: the leaderboard is reached through its MatchServices.
            CANEventManager?.Leaderboard?.Dispose();
            CANEventManager?.Dispose();
            signupWindows?.Dispose();
            eventScheduler?.Dispose();
            adminPanel?.Dispose();
            if (loadoutPickGui != null && ClientNetworkHandler != null)
                ClientNetworkHandler.OnLoadoutPickRequested -= loadoutPickGui.OpenForMatchStart;
            if (ClientNetworkHandler != null)
            {
                if (_onTournamentReady != null)
                    ClientNetworkHandler.OnTournamentReadyRequested -= _onTournamentReady;
                if (_onTournamentReadyClosed != null)
                    ClientNetworkHandler.OnTournamentReadyClosed -= _onTournamentReadyClosed;
            }
            eventBrowserGui?.Dispose();
            loadoutPickGui?.Dispose();
            tournamentReadyGui?.Dispose();
            eventInviteHud?.Dispose();
            hudLayoutEditor?.Dispose();
            eventHudGui?.Dispose();
            // Unregister before disposing, or the next frame draws meshes we just freed.
            if (ctfFlagRenderer != null)
            {
                clientApi?.Event.UnregisterRenderer(ctfFlagRenderer, EnumRenderStage.Opaque);
                ctfFlagRenderer.Dispose();   // frees the uploaded flag meshes
            }
            if (hgZoneDomeRenderer != null)
            {
                clientApi?.Event.UnregisterRenderer(hgZoneDomeRenderer, EnumRenderStage.OIT);
                hgZoneDomeRenderer.Dispose();   // frees the uploaded dome mesh
            }
            try { harmony?.UnpatchAll("canevents.respawn"); } catch { }
            RespawnPatch.Mod = null;
            base.Dispose();
        }
    }
}
