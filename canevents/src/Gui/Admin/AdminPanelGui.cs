using System;
using System.Collections.Generic;
using canevents.src.Gui.Admin.Dialogs;
using canevents.src.Gui.Admin.Pages;
using canevents.src.Gui.Admin.Widgets;
using canevents.src.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace canevents.src.Gui.Admin
{
    // Admin panel (F7). GuiComposer trees are immutable, so every change rebuilds the whole window.
    public class AdminPanelGui : GuiDialog
    {
        public const string HotkeyCode = "canevents_admin";
        private const string ComposerKey = "canevents-admin";

        public const double DialogWidth = 900;
        public const double DialogHeight = 600;
        private const double StatusHeight = 26;
        private const double ContentTop = 6;

        private static readonly PanelTab[] TabOrder =
        {
            PanelTab.Dashboard, PanelTab.Events, PanelTab.Arenas, PanelTab.NewArena, PanelTab.Content,
            PanelTab.Economy, PanelTab.Tournaments, PanelTab.Players, PanelTab.Debug, PanelTab.Help
        };

        private readonly ClientNetworkHandler networkHandler;
        private readonly PlaceHighlighter highlighter;
        private readonly PageRegistry pages = new PageRegistry();
        private readonly Action<PlaceOperationResultMsg> onPlaceOperationResult;
        private readonly Action onWatchedChanged;

        private Action unwatch;
        private CANGuiPage shownPage;

        private bool rebuildPending;
        private CANGuiDialogPanel sideDialog;

        private string statusText;
        private bool statusOk = true;

        private readonly List<(string time, bool ok, string text)> actionLog = new List<(string, bool, string)>();

        public AdminGuiState State { get; } = new AdminGuiState();
        public ClientNetworkHandler Net => networkHandler;
        public ICoreClientAPI Capi => capi;
        public PlaceHighlighter Highlighter => highlighter;
        public IReadOnlyList<(string time, bool ok, string text)> ActionLog => actionLog;
        public long NowMs => capi.ElapsedMilliseconds;

        public ElementBounds MainDialogBounds => Composers[ComposerKey]?.Bounds;

        public AdminPanelGui(ICoreClientAPI capi, ClientNetworkHandler networkHandler, PlaceHighlighter highlighter)
            : base(capi)
        {
            this.networkHandler = networkHandler;
            this.highlighter = highlighter;

            capi.Input.RegisterHotKey(HotkeyCode, Lang.Get("canevents:admin-hotkey"), GlKeys.F7, HotkeyType.GUIOrOtherControls);
            capi.Input.SetHotKeyHandler(HotkeyCode, _ =>
            {
                if (IsOpened()) TryClose();
                else TryOpen();
                return true;
            });

            onPlaceOperationResult = msg =>
            {
                if (msg == null) return;
                SetStatus(msg.Success, msg.Message);
                LogAction(msg.Success, msg.Message);
            };
            onWatchedChanged = () => { if (IsOpened()) RequestRebuild(); };

            networkHandler.OnPlaceOperationResult += onPlaceOperationResult;
        }

        public override string ToggleKeyCombinationCode => HotkeyCode;

        // Server silently drops admin packets from non-controlserver players.
        public override bool TryOpen()
        {
            if (!(capi.World?.Player?.HasPrivilege(Privilege.controlserver) ?? false))
            {
                capi.TriggerIngameError(this, "noprivilege", Lang.Get("canevents:admin-no-privilege"));
                return false;
            }
            return base.TryOpen();
        }

        public override void OnGuiOpened()
        {
            base.OnGuiOpened();
            // Grab the crosshair target before the cursor is freed, after that the engine picks under the mouse.
            highlighter.CaptureAim();
            ShowPage(State.Tab);
            Rebuild();
        }

        public override void OnGuiClosed()
        {
            base.OnGuiClosed();
            HidePage();
            CloseSideDialog();
            State.ArmedKey = null;
            rebuildPending = false;
            // Outline stays after close, only the feet marker goes.
            highlighter.UpdatePlacementMarker(this, false, -1);
        }

        public override void OnRenderGUI(float deltaTime)
        {
            // Rebuild before base draw so this frame uses the new composer.
            if (ArmedTextButton.ExpireIfStale(State, NowMs)) rebuildPending = true;
            if (rebuildPending && !TextInputFocused()) Rebuild();

            base.OnRenderGUI(deltaTime);

            if (shownPage != null)
            {
                bool want = shownPage.WantsPlacementMarker(out int tierIdx);
                highlighter.UpdatePlacementMarker(this, want, tierIdx);
            }
        }

        public override void Dispose()
        {
            networkHandler.OnPlaceOperationResult -= onPlaceOperationResult;
            HidePage();
            try { highlighter.UpdatePlacementMarker(this, false, -1); } catch { }
            base.Dispose();
        }

        public void SelectTab(PanelTab tab)
        {
            if (tab == State.Tab && shownPage != null) return;
            State.ArmedKey = null;
            CloseSideDialog();
            State.Tab = tab;
            ShowPage(tab);
            RequestRebuild();
        }

        private void ShowPage(PanelTab tab)
        {
            HidePage();

            shownPage = pages.Get(tab);
            shownPage.Attach(this);

            var watch = new PageWatchList();
            shownPage.Watch(watch);
            foreach (var (subscribe, unsubscribe) in watch.Entries)
            {
                subscribe(onWatchedChanged);
                unwatch += () => unsubscribe(onWatchedChanged);
            }

            AdminDataRequests.RequestDataFor(networkHandler, tab);
            shownPage.OnShown();
        }

        private void HidePage()
        {
            unwatch?.Invoke();
            unwatch = null;
            shownPage?.OnHidden();
            shownPage = null;
        }

        private void OnTabClicked(int index)
        {
            if (index < 0 || index >= TabOrder.Length) return;
            SelectTab(TabOrder[index]);
        }

        // Deferred to the next frame, never inside the click/packet handler.
        public void RequestRebuild()
        {
            if (!IsOpened()) return;
            rebuildPending = true;
        }

        private bool TextInputFocused()
        {
            foreach (var compo in Composers.Values)
            {
                if (compo?.CurrentTabIndexElement is GuiElementEditableTextBase) return true;
            }
            return false;
        }

        private void Rebuild()
        {
            rebuildPending = false;
            if (shownPage == null) ShowPage(State.Tab);

            var tabs = new GuiTab[TabOrder.Length];
            for (int i = 0; i < TabOrder.Length; i++)
            {
                var t = TabOrder[i];
                string name = Lang.Get("canevents:admin-tab-" + t.ToString().ToLowerInvariant());
                tabs[i] = new GuiTab { Name = name, DataInt = i };
            }

            var tabFont = CairoFont.WhiteDetailText();
            // Sits the row on the title bar strip, as the events browser does.
            var tabBounds = ElementBounds.Fixed(5, -24, DialogWidth - 5, 25);

            var bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
            bgBounds.BothSizing = ElementSizing.FitToChildren;
            // Fixed-size child so the window doesn't resize between tabs.
            bgBounds.WithChildren(ElementBounds.Fixed(0, 0, DialogWidth, DialogHeight));

            var dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);

            var compo = capi.Gui
                .CreateCompo(ComposerKey, dialogBounds)
                .AddShadedDialogBG(bgBounds)
                .AddDialogTitleBarWithBg(Lang.Get("canevents:admin-title"), () => TryClose())
                .AddHorizontalTabs(tabs, tabBounds, OnTabClicked, tabFont,
                    tabFont.Clone().WithColor(GuiStyle.ActiveButtonTextColor), "tabs")
                .BeginChildElements(bgBounds);

            Composers[ComposerKey] = compo;
            compo.GetHorizontalTabs("tabs").activeElement = Math.Max(0, Array.IndexOf(TabOrder, State.Tab));

            double contentHeight = DialogHeight - ContentTop - StatusHeight - 8;
            var main = ElementBounds.Fixed(0, ContentTop, DialogWidth, contentHeight);
            var ctx = new PageBuildContext
            {
                Gui = this,
                State = State,
                Compo = compo,
                Main = main,
                Current = ElementBounds.Fixed(0, ContentTop, DialogWidth, 0),
                Line = ElementBounds.Fixed(0, ContentTop, DialogWidth, 0)
            };

            try
            {
                shownPage.Build(this, ctx);
            }
            catch (Exception e)
            {
                capi.Logger.Error("[canevents] Admin page {0} failed to build: {1}", State.Tab, e);
                compo.AddStaticText(Lang.Get("canevents:admin-page-error", e.Message),
                    CairoFont.WhiteSmallText().WithColor(AdminColors.Danger),
                    ElementBounds.Fixed(0, ContentTop + 20, DialogWidth, 60), "page-error");
            }

            ComposeStatusBar(compo);

            // Compose(true) focuses the first text field, which would then block every later rebuild.
            compo.EndChildElements().Compose(false);
            ctx.RunAfterCompose();
            UpdateStatusLine();

            sideDialog?.Build(this);
        }

        private void ComposeStatusBar(GuiComposer compo)
        {
            double y = DialogHeight - StatusHeight;
            double x = 0;

            if (highlighter.AnythingDrawn || !highlighter.Enabled)
            {
                string caption = Lang.Get(highlighter.Enabled ? "canevents:admin-outline-hide" : "canevents:admin-outline-show");
                double w = ButtonRow.ButtonWidth(caption) - 10;
                var b = ElementBounds.Fixed(x, y, w, 22);
                compo.AddSmallButton(caption, new ActionConsumable(() =>
                {
                    // Only hides the drawing; the marks stay for the bounds editor.
                    highlighter.SetEnabled(!highlighter.Enabled);
                    RequestRebuild();
                    return true;
                }), b, EnumButtonStyle.Small, "status-outline");
                Tooltip.Add(compo, Lang.Get(highlighter.Enabled ? "canevents:admin-outline-hide-hint" : "canevents:admin-outline-show-hint"),
                    b, "status-outline-tip");
                x += w + 8;
            }

            string logCaption = Lang.Get("canevents:admin-log-button");
            double logWidth = ButtonRow.ButtonWidth(logCaption) - 10;
            var logBounds = ElementBounds.Fixed(DialogWidth - logWidth, y, logWidth, 22);
            compo.AddSmallButton(logCaption, new ActionConsumable(() =>
            {
                OpenSideDialog(new ActionLogPanel());
                return true;
            }), logBounds, EnumButtonStyle.Small, "status-log");

            compo.AddDynamicText("", CairoFont.WhiteSmallText(),
                ElementBounds.Fixed(x, y + 3, DialogWidth - x - logWidth - 10, 20), "status-text");
        }

        // Updated in place, no rebuild, so it works while typing.
        private void UpdateStatusLine()
        {
            var text = Composers[ComposerKey]?.GetDynamicText("status-text");
            if (text == null) return;

            string shown;
            double[] color;
            if (networkHandler.ProtocolMismatch)
            {
                // Server drops our packets on mismatch.
                shown = Lang.Get("canevents:admin-status-protocol-mismatch");
                color = AdminColors.Danger;
            }
            else if (!string.IsNullOrEmpty(statusText))
            {
                shown = statusText;
                color = statusOk ? AdminColors.Success : AdminColors.Danger;
            }
            else
            {
                shown = Lang.Get("canevents:admin-status-idle");
                color = AdminColors.Muted;
            }

            text.Font = CairoFont.WhiteSmallText().WithColor(color);
            text.SetNewText(Card.Fit(shown, text.Font, text.Bounds.fixedWidth), false, true);
        }

        public void SetStatus(bool ok, string message)
        {
            statusOk = ok;
            statusText = (ok ? Lang.Get("canevents:admin-status-ok") : Lang.Get("canevents:admin-status-error"))
                         + " " + message;
            if (IsOpened()) UpdateStatusLine();
        }

        public void LogAction(bool ok, string text)
        {
            actionLog.Add((DateTime.Now.ToString("HH:mm:ss"), ok, text));
            if (actionLog.Count > 30) actionLog.RemoveAt(0);
            if (sideDialog is ActionLogPanel) RequestRebuild();
        }

        public void Act(string log, Action send)
        {
            send();
            LogAction(true, log);
        }

        public void OpenSideDialog(CANGuiDialogPanel panel)
        {
            sideDialog = panel;
            if (IsOpened()) panel.Build(this);
        }

        public void CloseSideDialog()
        {
            sideDialog = null;
            // Remove alone leaks textures until a composer with the same key is created again.
            Composers[DialogFrame.ComposerKey]?.Dispose();
            Composers.Remove(DialogFrame.ComposerKey);
        }
    }
}
