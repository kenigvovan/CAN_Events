using System;
using System.Collections.Generic;
using canevents.src.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace canevents.src.Gui.Native
{
    // GuiComposer trees are immutable, so the dialog recomposes on packets. Tabs live in the partials.
    public partial class EventBrowserGui : GuiDialog
    {
        public const string HotkeyCode = "canevents_browser";

        private readonly ClientNetworkHandler networkHandler;
        private int curTab;

        // Kept for unsubscribing: the network handler outlives this dialog across a hot-reload.
        private readonly Action onEventList;
        private readonly Action onLeaderboard;
        private readonly Action onHistory;
        private readonly Action onParty;
        private readonly Action onDefaults;
        private readonly Action<EventResultMsg> onResult;
        private readonly Action onTournaments;
        private readonly Action onBracket;
        private readonly Action<string> onHotkeyPush;
        private readonly Action<EventInviteMsg> onInvite;
        private readonly Action<EventInviteClosedMsg> onInviteEnded;
        private readonly Action onInventoryAudit;
        private readonly Action onInventoryDetail;

        // Wired up in canevents.cs.
        public Action OnEditHudLayout { get; set; }

        // Shared with the match-start flow; the server can raise it with the browser closed.
        public LoadoutPickGui LoadoutPick { get; set; }

        public EventBrowserGui(ICoreClientAPI capi, ClientNetworkHandler networkHandler, string defaultHotkey = "K")
            : base(capi)
        {
            this.networkHandler = networkHandler;

            if (!Enum.TryParse<GlKeys>(defaultHotkey, true, out var hotkey))
            {
                capi.Logger.Warning("[canevents] Invalid mainDialogHotkey '{0}', falling back to K.", defaultHotkey);
                hotkey = GlKeys.K;
            }
            capi.Input.RegisterHotKey(HotkeyCode, Lang.Get("canevents:gui-browser-title"), hotkey, HotkeyType.GUIOrOtherControls);
            capi.Input.SetHotKeyHandler(HotkeyCode, _ => TryOpen());

            onEventList  = () => RecomposeIfOpen();
            onLeaderboard = () => RecomposeIfOpen();
            onHistory    = () => RecomposeIfOpen();
            onParty      = () => RecomposeIfOpen();
            onDefaults   = () => RecomposeIfOpen();
            onResult     = _ => RecomposeIfOpen();
            onTournaments = () => RecomposeIfOpen();
            onBracket    = () => RecomposeIfOpen();
            onHotkeyPush = ApplyServerDefaultHotkey;
            // Sign-up windows change card order and the countdown line, so recompose.
            onInvite      = _ => RecomposeIfOpen();
            onInviteEnded = _ => RecomposeIfOpen();

            networkHandler.EventList.Changed += onEventList;
            networkHandler.Leaderboard.Changed += onLeaderboard;
            networkHandler.MatchHistoryData.Changed += onHistory;
            networkHandler.PartyState.Changed += onParty;
            networkHandler.LoadoutDefaults.Changed += onDefaults;
            networkHandler.OnResultReceived += onResult;
            networkHandler.TournamentList.Changed += onTournaments;
            networkHandler.TournamentBracket.Changed += onBracket;
            networkHandler.OnMainDialogHotkeyReceived += onHotkeyPush;
            networkHandler.OnEventInvited += onInvite;
            networkHandler.OnEventInviteClosed += onInviteEnded;

            // Admins only; the server refuses everyone else.
            onInventoryAudit = () => RecomposeIfOpen();
            onInventoryDetail = () => RecomposeIfOpen();
            networkHandler.InventoryAudit.Changed += onInventoryAudit;
            networkHandler.InventoryBackupDetail.Changed += onInventoryDetail;
            if (networkHandler.MainDialogHotkey != null) ApplyServerDefaultHotkey(networkHandler.MainDialogHotkey);
        }

        public override string ToggleKeyCombinationCode => HotkeyCode;

        // No live feed; the server answers these on request.
        public override void OnGuiOpened()
        {
            base.OnGuiOpened();
            networkHandler.RequestEventList();
            networkHandler.RequestLeaderboard();
            networkHandler.RequestMatchHistory();
            networkHandler.RequestPartyState();
            networkHandler.RequestLoadoutDefaults();
            // History tab needs archived tournaments.
            networkHandler.RequestTournamentList(includeHistory: true);
            networkHandler.RequestDuelTargets();
            // The sweep reads player data server-side; only when reopening on that tab.
            if (curTab == InventoryTab && MayAuditInventories) RequestInventoryAudit();
            ComposeDialog();
        }

        public override void OnRenderGUI(float deltaTime)
        {
            base.OnRenderGUI(deltaTime);
            // These tick without packets.
            UpdateSignupNotice();
            UpdateQueueNotice();
        }

        private void RecomposeIfOpen()
        {
            if (IsOpened()) ComposeDialog();
        }

        private void ComposeDialog()
        {
            var tabList = new List<GuiTab>
            {
                new GuiTab { Name = Lang.Get("canevents:gui-tab-ranked"),      DataInt = 0 },
                new GuiTab { Name = Lang.Get("canevents:gui-tab-casual"),      DataInt = 1 },
                new GuiTab { Name = Lang.Get("canevents:gui-tab-party"),       DataInt = 2 },
                new GuiTab { Name = Lang.Get("canevents:gui-tab-leaderboard"), DataInt = 3 },
                new GuiTab { Name = Lang.Get("canevents:gui-tab-history"),     DataInt = 4 },
                new GuiTab { Name = Lang.Get("canevents:gui-tab-tournaments"), DataInt = 5 },
                new GuiTab { Name = Lang.Get("canevents:gui-tab-duels"),       DataInt = 6 },
            };

            // Last so other tab indices don't shift. Server re-checks permissions anyway.
            if (MayAuditInventories)
                tabList.Add(new GuiTab { Name = Lang.Get("canevents:gui-tab-inventory"), DataInt = InventoryTab });

            var tabs = tabList.ToArray();

            // Tab may have disappeared (privilege lost).
            if (curTab >= tabs.Length) curTab = 0;

            var tabFont = CairoFont.WhiteDetailText();
            // x=5 keeps the first tab inside the edge; y=-24 puts the row on the 31px title bar.
            var tabBounds = ElementBounds.Fixed(5, -24, DialogWidth - 5, 25);

            var bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
            bgBounds.BothSizing = ElementSizing.FitToChildren;

            var dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);

            SingleComposer = capi.Gui
                .CreateCompo("canevents-browser", dialogBounds)
                .AddShadedDialogBG(bgBounds)
                // WithBg: the plain title bar is transparent and leaves a gap under the tabs.
                .AddDialogTitleBarWithBg(Lang.Get("canevents:gui-browser-title"), OnTitleBarClose)
                .AddHorizontalTabs(tabs, tabBounds, OnTabClicked, tabFont,
                    tabFont.Clone().WithColor(GuiStyle.ActiveButtonTextColor), "tabs")
                .BeginChildElements(bgBounds);

            SingleComposer.GetHorizontalTabs("tabs").activeElement = curTab;

            // Same height on every tab so the window doesn't jump when switching.
            MinListHeight = MaxListHeight;

            SingleColumnCards = curTab == 2 || curTab == 3 || curTab == 4 || curTab == 6
                                || curTab == InventoryTab;

            switch (curTab)
            {
                case 0: ComposeEventsTab(rated: true); break;
                case 1: ComposeEventsTab(rated: false); break;
                case 2: ComposePartyTab(); break;
                case 3: ComposeLeaderboardTab(); break;
                case 4: ComposeHistoryTab(); break;
                case 6: ComposeDuelsTab(); break;
                case InventoryTab: ComposeInventoryTab(); break;
                default: ComposeTournamentTab(); break;
            }

            ComposeFooter();

            SingleComposer.EndChildElements().Compose();
            RestoreInventoryTabState();
            UpdateScrollbar();
            UpdateBracketScrollbar();

            // Row heights are known only after Compose; at most one extra pass.
            if (!adjustingViewport && AdjustViewport())
            {
                adjustingViewport = true;
                ComposeDialog();
                adjustingViewport = false;
            }
        }

        private bool adjustingViewport;

        private void ComposeFooter()
        {
            // Per-tab buttons own the left side.
            var right = ElementBounds.Fixed(EnumDialogArea.RightFixed, 0, ControlsY, 0, 0)
                .WithFixedPadding(9, 4);

            if (OnEditHudLayout != null)
            {
                SingleComposer.AddSmallButton(Lang.Get("canevents:hud-edit-open"),
                    OnOpenHudEditor, right, EnumButtonStyle.Normal);
                right = ElementBounds.Fixed(EnumDialogArea.RightFixed, 0, ControlsY, 0, 0)
                    .WithFixedPadding(9, 4)
                    .WithFixedAlignmentOffset(-150, 0);
            }

            if (capi.World?.Player?.WorldData?.CurrentGameMode == EnumGameMode.Spectator)
            {
                SingleComposer.AddSmallButton(Lang.Get("canevents:spectator-leave-button"),
                    OnLeaveSpectator, right, EnumButtonStyle.Normal);
            }
        }

        private bool OnLeaveSpectator()
        {
            // Covers both eliminated players and observers.
            networkHandler.SendStopSpectating();
            return true;
        }

        private bool OnOpenHudEditor()
        {
            // Caught and logged: this once crashed the client with nothing in the log.
            var log = HudLayoutEditorGui.LogFor(capi);
            try
            {
                log.Notification("button pressed, closing browser");
                TryClose();
                OnEditHudLayout?.Invoke();
                log.Notification("editor open requested");
            }
            catch (Exception e)
            {
                log.Error("opening from the browser failed: {0}", e);
            }
            return true;
        }

        private void OnTabClicked(int index)
        {
            curTab = index;
            scrollOffset = 0f;

            // These go stale while the window is open; refresh on tab switch.
            if (curTab == 6) networkHandler.RequestDuelTargets();

            if (curTab == InventoryTab && MayAuditInventories)
            {
                invDetailUid = null;
                invConfirmingUid = null;
                RequestInventoryAudit();
            }

            ComposeDialog();
        }

        private void OnTitleBarClose() => TryClose();

        // Current and default mapping kept in sync so VS doesn't persist the server key as a user override.
        private void ApplyServerDefaultHotkey(string keyName)
        {
            if (string.IsNullOrWhiteSpace(keyName)) return;
            if (!Enum.TryParse<GlKeys>(keyName, true, out var key))
            {
                capi.Logger.Warning("[canevents] Server sent invalid mainDialogHotkey '{0}', ignoring.", keyName);
                return;
            }

            var hk = capi.Input.GetHotKeyByCode(HotkeyCode);
            if (hk == null) return;

            bool playerRebound = hk.CurrentMapping != null && hk.DefaultMapping != null
                && hk.CurrentMapping.KeyCode != hk.DefaultMapping.KeyCode;
            if (playerRebound) return;

            hk.CurrentMapping = new KeyCombination { KeyCode = (int)key };
            hk.DefaultMapping = new KeyCombination { KeyCode = (int)key };
        }

        public override void Dispose()
        {
            base.Dispose();
            networkHandler.EventList.Changed -= onEventList;
            networkHandler.Leaderboard.Changed -= onLeaderboard;
            networkHandler.MatchHistoryData.Changed -= onHistory;
            networkHandler.PartyState.Changed -= onParty;
            networkHandler.LoadoutDefaults.Changed -= onDefaults;
            networkHandler.OnResultReceived -= onResult;
            networkHandler.TournamentList.Changed -= onTournaments;
            networkHandler.TournamentBracket.Changed -= onBracket;
            networkHandler.OnMainDialogHotkeyReceived -= onHotkeyPush;
            networkHandler.OnEventInvited -= onInvite;
            networkHandler.OnEventInviteClosed -= onInviteEnded;
            networkHandler.InventoryAudit.Changed -= onInventoryAudit;
            networkHandler.InventoryBackupDetail.Changed -= onInventoryDetail;
        }
    }
}
