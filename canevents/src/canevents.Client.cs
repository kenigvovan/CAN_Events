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
        public override void StartClientSide(ICoreClientAPI api)
        {
            // First, so the unhandled-exception hook catches everything after it.
            ClientTrace.Install(api);

            // Client owns the camera height. In singleplayer the server side already patched it.
            clientHarmony = new Harmony(HarmonyId);
            HarmonyPatches.SpectatorBodyMetricsPatch.Apply(clientHarmony);

            ClientNetworkHandler = new Network.ClientNetworkHandler(api);

            hudLayout = Gui.HudLayout.Load(api);
            eventHudGui = new Gui.Native.EventHudGui(api, ClientNetworkHandler, hudLayout);
            hudLayoutEditor = new Gui.Native.HudLayoutEditorGui(api, hudLayout, () =>
            {
                eventHudGui.OnLayoutChanged();
                eventInviteHud?.OnLayoutChanged();
                shopPointsHud?.OnLayoutChanged();
            });
            loadoutPickGui = new Gui.Native.LoadoutPickGui(api, ClientNetworkHandler);
            duelChallengeGui = new Gui.Native.DuelChallengeGui(api, ClientNetworkHandler);
            eventBrowserGui = new Gui.Native.EventBrowserGui(api, ClientNetworkHandler)
            {
                LoadoutPick = loadoutPickGui,
                DuelChallenge = duelChallengeGui,
                OnEditHudLayout = () => hudLayoutEditor.TryOpen()
            };
            duelChallengeGui.OnCancelled = () => eventBrowserGui.TryOpen();
            tournamentReadyGui = new Gui.Native.TournamentReadyGui(api, ClientNetworkHandler);
            // HUD, not a dialog, so it doesn't grab the mouse.
            eventInviteHud = new Gui.Native.EventInviteHud(api, ClientNetworkHandler, hudLayout)
            {
                LoadoutPick = loadoutPickGui
            };
            loadoutPickGui.OnQueuePickCancelled = () => eventBrowserGui.TryOpen();
            // Opens itself from the spectate state packet.
            spectatorPanel = new Gui.Native.SpectatorPanelGui(api, ClientNetworkHandler);

            duelPrompt = new Gui.Native.DuelPromptGui(api, ClientNetworkHandler);
            matchReadyGui = new Gui.Native.MatchReadyGui(api, ClientNetworkHandler);
            teamPickGui = new Gui.Native.TeamPickGui(api, ClientNetworkHandler);

            inventoryAuditGui = new Gui.Native.InventoryAuditGui(api, ClientNetworkHandler);
            shopPreviewGui = new Gui.Native.ShopPreviewGui(api, ClientNetworkHandler);
            matchShopGui = new Gui.Native.MatchShopGui(api, ClientNetworkHandler) { Preview = shopPreviewGui };
            loadoutPickGui.ShopPreview = shopPreviewGui;
            // Subscribes itself; opens with the first shop state of a match.
            shopPointsHud = new Gui.Native.ShopPointsHud(api, ClientNetworkHandler, hudLayout);

            api.Gui.RegisterDialog(eventHudGui, eventBrowserGui, loadoutPickGui, hudLayoutEditor,
                tournamentReadyGui, eventInviteHud, spectatorPanel, duelPrompt, matchReadyGui,
                inventoryAuditGui, duelChallengeGui, teamPickGui, matchShopGui, shopPointsHud, shopPreviewGui);

            WireNativeDialogs(ClientNetworkHandler);

            // Return false when not spectating so the keys stay usable for other bindings.
            api.Input.RegisterHotKey(Gui.Native.SpectatorPanelGui.NextHotkeyCode,
                Vintagestory.API.Config.Lang.Get("canevents:hotkey-spectate-next"), GlKeys.BracketRight,
                HotkeyType.GUIOrOtherControls);
            api.Input.SetHotKeyHandler(Gui.Native.SpectatorPanelGui.NextHotkeyCode,
                _ => spectatorPanel.TryStep(1));

            api.Input.RegisterHotKey(Gui.Native.SpectatorPanelGui.PrevHotkeyCode,
                Vintagestory.API.Config.Lang.Get("canevents:hotkey-spectate-prev"), GlKeys.BracketLeft,
                HotkeyType.GUIOrOtherControls);
            api.Input.SetHotKeyHandler(Gui.Native.SpectatorPanelGui.PrevHotkeyCode,
                _ => spectatorPanel.TryStep(-1));

            // Join/dismiss keys for the signup panel, rebindable in Controls.
            api.Input.RegisterHotKey(Gui.Native.EventInviteHud.JoinHotkeyCode,
                Vintagestory.API.Config.Lang.Get("canevents:hotkey-join"), GlKeys.G,
                HotkeyType.GUIOrOtherControls);
            api.Input.SetHotKeyHandler(Gui.Native.EventInviteHud.JoinHotkeyCode,
                _ => eventInviteHud.TryToggleQueue());

            api.Input.RegisterHotKey(Gui.Native.EventInviteHud.DismissHotkeyCode,
                Vintagestory.API.Config.Lang.Get("canevents:hotkey-dismiss"), GlKeys.G,
                HotkeyType.GUIOrOtherControls, shiftPressed: true);
            api.Input.SetHotKeyHandler(Gui.Native.EventInviteHud.DismissHotkeyCode,
                _ => eventInviteHud.TryDismissCurrent());

            // Match shop; does nothing outside a match.
            api.Input.RegisterHotKey(Gui.Native.MatchShopGui.HotkeyCode,
                Vintagestory.API.Config.Lang.Get("canevents:hotkey-shop"), GlKeys.B,
                HotkeyType.GUIOrOtherControls);
            api.Input.SetHotKeyHandler(Gui.Native.MatchShopGui.HotkeyCode, _ => matchShopGui.OnHotkey());

            // Admin panel (F7).
            placeHighlighter = new Gui.PlaceHighlighter(api, ClientNetworkHandler);
            adminPanelGui = new Gui.Admin.AdminPanelGui(api, ClientNetworkHandler, placeHighlighter);
            api.Gui.RegisterDialog(adminPanelGui);

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
                        shopPointsHud?.OnLayoutChanged();
                        return TextCommandResult.Success(Vintagestory.API.Config.Lang.Get("canevents:hud-edit-reset-done"));
                    })
                .EndSubCommand()
                // Signup toast size: full, mini or off (the browser still shows the countdown).
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

            // Per-player trace toggle; only writes to their own log, so no privilege.
            api.ChatCommands.Create("cantrace")
                .WithDescription(Vintagestory.API.Config.Lang.Get("canevents:trace-desc"))
                .WithArgs(api.ChatCommands.Parsers.OptionalBool("on"))
                .HandleWith(args =>
                {
                    bool on = args.Parsers[0].IsMissing
                        ? !ClientTrace.Enabled
                        : (bool)args.Parsers[0].GetValue();
                    ClientTrace.SetEnabled(on);
                    return TextCommandResult.Success(Vintagestory.API.Config.Lang.Get(
                        on ? "canevents:trace-on" : "canevents:trace-off"));
                });

            // Registered for the whole session: no client signal for CTF start.
            clientApi = api;
            ctfFlagRenderer = new Gui.CtfFlagRenderer(api, ClientNetworkHandler);
            api.Event.RegisterRenderer(ctfFlagRenderer, EnumRenderStage.Opaque, "canevents-ctfflag");

            // OIT stage: the shader writes the OIT buffers.
            hgZoneDomeRenderer = new Gui.HgZoneDomeRenderer(api, ClientNetworkHandler);
            api.Event.RegisterRenderer(hgZoneDomeRenderer, EnumRenderStage.OIT, "canevents-hgzonedome");
        }

        // Server-opened dialogs, each paired with its unsubscribe in unwireNativeDialogs.
        private void WireNativeDialogs(Network.ClientNetworkHandler net)
        {
            Action<Network.LoadoutPickRequestMsg> onLoadoutPick = loadoutPickGui.OpenForMatchStart;
            net.OnLoadoutPickRequested += onLoadoutPick;
            unwireNativeDialogs += () => { net.OnLoadoutPickRequested -= onLoadoutPick; };

            Action<Network.TournamentReadyRequestMsg> onTournamentReady = msg => tournamentReadyGui.OpenFor(msg);
            Action<Network.TournamentReadyClosedMsg> onTournamentReadyClosed =
                msg => tournamentReadyGui.CloseFor(msg.TournamentId, msg.MatchId);
            net.OnTournamentReadyRequested += onTournamentReady;
            net.OnTournamentReadyClosed += onTournamentReadyClosed;
            unwireNativeDialogs += () =>
            {
                net.OnTournamentReadyRequested -= onTournamentReady;
                net.OnTournamentReadyClosed -= onTournamentReadyClosed;
            };

            Action onReadyPrompt = matchReadyGui.OnPromptChanged;
            net.OnReadyPromptChanged += onReadyPrompt;
            unwireNativeDialogs += () => { net.OnReadyPromptChanged -= onReadyPrompt; };

            Action onTeamPick = teamPickGui.OnStateChanged;
            net.OnTeamPickChanged += onTeamPick;
            unwireNativeDialogs += () => { net.OnTeamPickChanged -= onTeamPick; };

            Action onDuelPrompt = duelPrompt.OnPromptChanged;
            net.OnDuelPromptChanged += onDuelPrompt;
            unwireNativeDialogs += () => { net.OnDuelPromptChanged -= onDuelPrompt; };

            Action onShopOpen = matchShopGui.OnOpenRequested;
            Action onShopChanged = matchShopGui.OnStateChanged;
            net.OnShopOpenRequested += onShopOpen;
            net.OnShopChanged += onShopChanged;
            unwireNativeDialogs += () =>
            {
                net.OnShopOpenRequested -= onShopOpen;
                net.OnShopChanged -= onShopChanged;
            };

            Action<Network.ShopPreviewMsg> onShopPreview = shopPreviewGui.OnPreview;
            net.OnShopPreview += onShopPreview;
            unwireNativeDialogs += () => { net.OnShopPreview -= onShopPreview; };

            Action onInventoryAudit = () => inventoryAuditGui.OpenPanel();
            net.OnInventoryAuditRequested += onInventoryAudit;
            unwireNativeDialogs += () => { net.OnInventoryAuditRequested -= onInventoryAudit; };
        }
    }
}
