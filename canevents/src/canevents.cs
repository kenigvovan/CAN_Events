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
    public partial class canevents : ModSystem
    {
        public PartyManager partyManager;
        public EventScheduler eventScheduler;
        public EventManagers.SignupWindows signupWindows;

        // Shared by both sides; in singleplayer each patch guards against double install.
        private const string HarmonyId = "canevents.respawn";

        public Config config;
        public Dictionary<string, Type> CANEventBehaviors = new Dictionary<string, Type>();
        List<EventDefinitionFile> eventDefinitions = new List<EventDefinitionFile>();
        // Shared with CANEventManager and TemplateEditor; refilled in place, never reassigned.
        List<EventPlacesManagers.ArenaTemplate> arenaTemplates = new List<EventPlacesManagers.ArenaTemplate>();
        List<CANEvent> Events;

        // Reload diffs against this so unchanged events keep their object (queues are keyed by it).
        private Dictionary<string, JToken> eventJsonByCode = new Dictionary<string, JToken>(StringComparer.OrdinalIgnoreCase);
        // File name -> code at last load. Ordinal: file name case-sensitivity depends on the OS.
        private Dictionary<string, string> eventCodeByFile = new Dictionary<string, string>(StringComparer.Ordinal);

        private sealed class EventDefinitionFile
        {
            public string FileName;
            public CANEventType Type;
            public JToken Json;
        }
        public CANEventManager CANEventManager { get; set; }
        // Server only.
        public PlaceTargeting PlaceTargeting { get; private set; }
        private Harmony harmony;
        private Harmony clientHarmony;
        private Bots.BotCleanup botCleanup;
        private EventManagers.IssuedGearDropWatcher issuedGearWatcher;
        private Tournaments.TournamentStore tournamentStore;
        private Tournaments.TournamentManager tournamentManager;

        // Null while the server is starting.
        public Duels.DuelManager Duels { get; private set; }
        public EventManagers.JoinPads JoinPads { get; private set; }
        public EventManagers.ArenaProtection ArenaProtection { get; private set; }
        private ICoreServerAPI _sapi;
        private PlayerDeathDelegate _onDeath;
        private EntityDeathDelegate _onEntityDeath;
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
            api.RegisterBlockBehaviorClass("CtfFlag", typeof(BlockBehaviors.BlockBehaviorCtfFlag));
            api.RegisterItemClass("ItemEventWand", typeof(Items.ItemEventWand));

            // Both sides, or the client logs a warning per bot.
            api.RegisterEntity("ArenaBot", typeof(Bots.EntityArenaBot));
            Vintagestory.GameContent.AiTaskRegistry.Register<Bots.AiTaskArenaBotSeek>("caneventsbotseek");
            Vintagestory.GameContent.AiTaskRegistry.Register<Bots.AiTaskArenaBotMelee>("caneventsbotmelee");
            Vintagestory.GameContent.AiTaskRegistry.Register<Bots.AiTaskArenaBotFlee>("caneventsbotflee");
        }

        public override void AssetsLoaded(ICoreAPI api)
        {
            base.AssetsLoaded(api);
            if (api is ICoreServerAPI)
            {
                eventDefinitions = LoadEventDefinitions(api, null, null);
                eventCodeByFile = eventDefinitions.ToDictionary(d => d.FileName, d => d.Type.Code, StringComparer.Ordinal);
                SeedDefaultSchematics(api);
                arenaTemplates.AddRange(LoadArenaTemplates(api, null, null));
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
        private Gui.Native.EventInviteHud eventInviteHud;
        private Gui.Native.SpectatorPanelGui spectatorPanel;
        private Gui.Native.DuelPromptGui duelPrompt;
        private Gui.Native.DuelChallengeGui duelChallengeGui;
        private Gui.Native.MatchReadyGui matchReadyGui;
        private Gui.Native.TeamPickGui teamPickGui;
        private Gui.Native.InventoryAuditGui inventoryAuditGui;
        private Gui.Native.MatchShopGui matchShopGui;
        private Gui.Native.ShopPointsHud shopPointsHud;
        private Gui.Native.ShopPreviewGui shopPreviewGui;
        private Action unwireNativeDialogs;
        private Gui.Admin.AdminPanelGui adminPanelGui;
        private Gui.PlaceHighlighter placeHighlighter;
        private Gui.CtfFlagRenderer ctfFlagRenderer;
        private Gui.HgZoneDomeRenderer hgZoneDomeRenderer;
        private ICoreClientAPI clientApi;   // kept only to unregister the renderer on dispose

        public override void Dispose()
        {
            if (_sapi != null)
            {
                _sapi.Event.PlayerDeath      -= _onDeath;
                _sapi.Event.OnEntityDeath    -= _onEntityDeath;
                _sapi.Event.PlayerRespawn    -= _onRespawn;
                _sapi.Event.PlayerDisconnect -= _onDisconnect;
                _sapi.Event.PlayerNowPlaying -= _onReconnect;
            }
            botCleanup?.Dispose();
            JoinPads?.Stop();
            ArenaProtection?.Stop();
            issuedGearWatcher?.Dispose();
            Duels?.Dispose();
            // Both before CANEventManager.Dispose.
            tournamentManager?.Dispose();
            ServerNetworkHandler?.Dispose();
            CANEventManager?.Leaderboard?.Dispose();
            CANEventManager?.Dispose();
            signupWindows?.Dispose();
            eventScheduler?.Dispose();
            adminPanelGui?.Dispose();
            placeHighlighter?.Dispose();
            // Unwire before disposing the dialogs.
            unwireNativeDialogs?.Invoke();
            unwireNativeDialogs = null;
            eventBrowserGui?.Dispose();
            loadoutPickGui?.Dispose();
            tournamentReadyGui?.Dispose();
            eventInviteHud?.Dispose();
            spectatorPanel?.Dispose();
            duelPrompt?.Dispose();
            duelChallengeGui?.Dispose();
            matchReadyGui?.Dispose();
            teamPickGui?.Dispose();
            inventoryAuditGui?.Dispose();
            matchShopGui?.Dispose();
            shopPointsHud?.Dispose();
            shopPreviewGui?.Dispose();
            hudLayoutEditor?.Dispose();
            eventHudGui?.Dispose();
            // Unregister first, or the next frame draws freed meshes.
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
            ClientTrace.Uninstall();
            try { harmony?.UnpatchAll(HarmonyId); } catch { }
            try { clientHarmony?.UnpatchAll(HarmonyId); } catch { }
            HarmonyPatches.SpectatorBodyMetricsPatch.Reset();
            RespawnPatch.Mod = null;
            base.Dispose();
        }
    }
}
