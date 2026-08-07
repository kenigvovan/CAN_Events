using System;
using canevents.src.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace canevents.src.Gui.Native
{
    /// <summary>
    /// The player-facing events dialog on the game's own GUI, unlike the ImGui
    /// <see cref="Gui.EventBrowserDialog"/>, which needs VSImGui installed on every client.
    ///
    /// GuiComposer builds an immutable element tree, so the whole dialog is recomposed whenever the
    /// data behind it changes - on packets, not per frame. Tabs live in the sibling partials.
    /// </summary>
    public partial class EventBrowserGui : GuiDialog
    {
        public const string HotkeyCode = "canevents_browser";

        private readonly ClientNetworkHandler networkHandler;
        private int curTab;

        // Detachable handlers: the network handler outlives this dialog across a hot-reload, and a
        // surviving subscription would recompose a dead dialog.
        private readonly Action onEventList;
        private readonly Action onLeaderboard;
        private readonly Action onHistory;
        private readonly Action onParty;
        private readonly Action onDefaults;
        private readonly Action<EventResultMsg> onResult;
        private readonly Action onTournaments;
        private readonly Action onBracket;
        private readonly Action<string> onHotkeyPush;

        /// <summary>Opens the HUD layout editor - wired up by the mod system, see canevents.cs.</summary>
        public Action OnEditHudLayout { get; set; }

        /// <summary>
        /// The class-picking dialog, shared with the match-start flow (the server can raise it with
        /// the browser closed). Set by the mod system.
        /// </summary>
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

            networkHandler.EventList.Changed += onEventList;
            networkHandler.Leaderboard.Changed += onLeaderboard;
            networkHandler.MatchHistoryData.Changed += onHistory;
            networkHandler.PartyState.Changed += onParty;
            networkHandler.LoadoutDefaults.Changed += onDefaults;
            networkHandler.OnResultReceived += onResult;
            networkHandler.TournamentList.Changed += onTournaments;
            networkHandler.TournamentBracket.Changed += onBracket;
            networkHandler.OnMainDialogHotkeyReceived += onHotkeyPush;
            if (networkHandler.MainDialogHotkey != null) ApplyServerDefaultHotkey(networkHandler.MainDialogHotkey);
        }

        public override string ToggleKeyCombinationCode => HotkeyCode;

        // Pulled fresh on open: there is no live feed, the server answers these on request.
        public override void OnGuiOpened()
        {
            base.OnGuiOpened();
            networkHandler.RequestEventList();
            networkHandler.RequestLeaderboard();
            networkHandler.RequestMatchHistory();
            networkHandler.RequestPartyState();
            networkHandler.RequestLoadoutDefaults();
            // With history: the History tab lists finished tournaments, already archived out of the
            // live list.
            networkHandler.RequestTournamentList(includeHistory: true);
            ComposeDialog();
        }

        private void RecomposeIfOpen()
        {
            if (IsOpened()) ComposeDialog();
        }

        private void ComposeDialog()
        {
            var tabs = new[]
            {
                new GuiTab { Name = Lang.Get("canevents:gui-tab-ranked"),      DataInt = 0 },
                new GuiTab { Name = Lang.Get("canevents:gui-tab-casual"),      DataInt = 1 },
                new GuiTab { Name = Lang.Get("canevents:gui-tab-party"),       DataInt = 2 },
                new GuiTab { Name = Lang.Get("canevents:gui-tab-leaderboard"), DataInt = 3 },
                new GuiTab { Name = Lang.Get("canevents:gui-tab-history"),     DataInt = 4 },
                new GuiTab { Name = Lang.Get("canevents:gui-tab-tournaments"), DataInt = 5 },
            };

            var tabFont = CairoFont.WhiteDetailText();
            // x=5 keeps the first tab inside the window edge; -24 sits the row directly on the 31px
            // title bar strip instead of over the gap a plain title bar leaves.
            var tabBounds = ElementBounds.Fixed(5, -24, DialogWidth - 5, 25);

            var bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
            bgBounds.BothSizing = ElementSizing.FitToChildren;

            var dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);

            SingleComposer = capi.Gui
                .CreateCompo("canevents-browser", dialogBounds)
                .AddShadedDialogBG(bgBounds)
                // WithBg: the plain title bar is transparent, leaving a gap between the tab row and
                // the top of the window frame.
                .AddDialogTitleBarWithBg(Lang.Get("canevents:gui-browser-title"), OnTitleBarClose)
                .AddHorizontalTabs(tabs, tabBounds, OnTabClicked, tabFont,
                    tabFont.Clone().WithColor(GuiStyle.ActiveButtonTextColor), "tabs")
                .BeginChildElements(bgBounds);

            SingleComposer.GetHorizontalTabs("tabs").activeElement = curTab;

            // How short a tab may shrink. Queue lists snap to content; rosters, ladders and brackets
            // are panels that should keep their shape as rows come and go.
            switch (curTab)
            {
                case 2: MinListHeight = 300; break;             // party
                case 3: MinListHeight = 360; break;             // leaderboard
                case 4: MinListHeight = 360; break;             // history
                case 5: MinListHeight = MaxListHeight; break;   // tournaments / bracket
                default: MinListHeight = 80; break;             // event lists hug their content
            }

            // Rosters and ladders are lists of rows, not a grid of choices.
            SingleColumnCards = curTab == 2 || curTab == 3 || curTab == 4;

            switch (curTab)
            {
                case 0: ComposeEventsTab(rated: true); break;
                case 1: ComposeEventsTab(rated: false); break;
                case 2: ComposePartyTab(); break;
                case 3: ComposeLeaderboardTab(); break;
                case 4: ComposeHistoryTab(); break;
                default: ComposeTournamentTab(); break;
            }

            ComposeFooter();

            SingleComposer.EndChildElements().Compose();
            UpdateScrollbar();
            UpdateBracketScrollbar();

            // Row heights are only known after Compose, so the snap is a second pass. Runs at most
            // once: the rows do not depend on the viewport height.
            if (!adjustingViewport && AdjustViewport())
            {
                adjustingViewport = true;
                ComposeDialog();
                adjustingViewport = false;
            }
        }

        private bool adjustingViewport;

        // Common bottom row: spectator escape hatch (only while spectating) and the HUD editor.
        private void ComposeFooter()
        {
            // Right-aligned: the per-tab buttons own the left side.
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
            // Via the chat command so the server-side rule stays in one place.
            capi.SendChatMessage("/canspectate off");
            return true;
        }

        private bool OnOpenHudEditor()
        {
            // Logged and caught: this button once took the whole client down with nothing in the log.
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
            ComposeDialog();
        }

        private void OnTitleBarClose() => TryClose();

        // Applies the server-supplied default key unless the player rebound it. Current and default
        // mapping are kept in sync so VS does not persist the server value as a user override, which
        // would stop later admin-config changes from propagating.
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
        }
    }
}
