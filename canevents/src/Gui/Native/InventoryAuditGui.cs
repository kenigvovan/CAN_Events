using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace canevents.src.Gui.Native
{
    // Standalone /eventadmin inv gui window. Cards shared with the browser's admin tab.
    public class InventoryAuditGui : GuiDialog
    {
        private const double DialogWidth = 680;
        private const double ListTop = 78;
        private const double MaxListHeight = 380;
        private const double ScrollbarWidth = 12;
        private const double ScrollbarGap = 10;

        private enum Mode { Holders, History, Detail }

        private readonly ClientNetworkHandler networkHandler;
        private readonly Action onList;
        private readonly Action onDetail;
        private readonly Action<PlaceOperationResultMsg> onResult;

        private Mode mode = Mode.Holders;
        private string nameFilter = "";
        private bool includeEveryone;

        private string detailUid;
        private bool detailParked;

        // Armed by the first hand-back click; any other action clears it.
        private string confirmingUid;

        // Null shows everyone's history.
        private string historyUid;
        private string historyName;

        private string lastResult;
        private bool lastResultOk;

        private float scrollOffset;
        private double viewportHeight = MaxListHeight;
        private ElementBounds clipBounds;
        private readonly List<EventCard> cards = new List<EventCard>();
        private bool laying;

        public InventoryAuditGui(ICoreClientAPI capi, ClientNetworkHandler networkHandler) : base(capi)
        {
            this.networkHandler = networkHandler;

            onList = () => { if (IsOpened()) Compose(); };
            onDetail = () =>
            {
                var msg = networkHandler.InventoryBackupDetail.Value;
                if (!IsOpened() || msg == null) return;
                detailUid = msg.PlayerUid;
                detailParked = msg.Parked;
                mode = Mode.Detail;
                scrollOffset = 0;
                Compose();
            };
            // Hand-back replies with the generic operation result.
            onResult = msg =>
            {
                if (!IsOpened() || msg == null) return;
                lastResult = msg.Message;
                lastResultOk = msg.Success;
                Compose();
            };

            networkHandler.InventoryAudit.Changed += onList;
            networkHandler.InventoryBackupDetail.Changed += onDetail;
            networkHandler.OnPlaceOperationResult += onResult;
        }

        public override void Dispose()
        {
            base.Dispose();
            networkHandler.InventoryAudit.Changed -= onList;
            networkHandler.InventoryBackupDetail.Changed -= onDetail;
            networkHandler.OnPlaceOperationResult -= onResult;
        }

        public override string ToggleKeyCombinationCode => null;

        public void OpenPanel()
        {
            mode = Mode.Holders;
            confirmingUid = null;
            scrollOffset = 0;
            Compose();
            TryOpen();
            Refresh();
        }

        public override void OnGuiOpened()
        {
            base.OnGuiOpened();
            Compose();
        }

        private void Refresh()
            => networkHandler.RequestInventoryAudit(
                string.IsNullOrWhiteSpace(nameFilter) ? null : nameFilter,
                includeEveryone,
                historyLimit: 150,
                historyUid: historyUid);

        private void Compose()
        {
            var composer = DialogShell.Begin(capi, "canevents-invaudit",
                Lang.Get("canevents:gui-invaudit-title"), () => TryClose());

            SingleComposer = composer;

            ComposeControls();
            BuildCards();
            ComposeCards();
            ComposeFooter();

            SingleComposer = composer.EndChildElements().Compose();

            SingleComposer.GetTextInput("filter")?.SetValue(nameFilter);
            SingleComposer.GetSwitch("everyone")?.SetValue(includeEveryone);

            UpdateScrollbar();

            // List height is known only after cells are measured; relayout once if the guess was wrong.
            if (!laying && AdjustViewport())
            {
                laying = true;
                Compose();
                laying = false;
            }
        }

        private void ComposeControls()
        {
            var font = CairoFont.WhiteSmallText();
            double y = DialogShell.BodyTop;

            SingleComposer.AddSmallButton(
                Lang.Get(mode == Mode.History ? "canevents:gui-invaudit-holders" : "canevents:gui-invaudit-history"),
                () =>
                {
                    mode = mode == Mode.History ? Mode.Holders : Mode.History;
                    scrollOffset = 0;
                    confirmingUid = null;
                    Compose();
                    return true;
                },
                ElementBounds.Fixed(EnumDialogArea.LeftFixed, 0, y, 0, 0).WithFixedPadding(10, 3),
                EnumButtonStyle.Normal);

            SingleComposer.AddSmallButton(Lang.Get("canevents:gui-refresh"),
                () => { Refresh(); return true; },
                ElementBounds.Fixed(EnumDialogArea.LeftFixed, 130, y, 0, 0).WithFixedPadding(10, 3),
                EnumButtonStyle.Normal);

            if (mode == Mode.History && historyUid != null)
            {
                SingleComposer.AddSmallButton(Lang.Get("canevents:gui-invaudit-history-all"),
                    () =>
                    {
                        historyUid = null;
                        historyName = null;
                        scrollOffset = 0;
                        Refresh();
                        Compose();
                        return true;
                    },
                    ElementBounds.Fixed(EnumDialogArea.LeftFixed, 260, y, 0, 0).WithFixedPadding(10, 3),
                    EnumButtonStyle.Normal);

                SingleComposer.AddStaticText(
                    Lang.Get("canevents:gui-invaudit-history-of", historyName ?? historyUid),
                    font.Clone().WithColor(Palette.Info),
                    ElementBounds.Fixed(430, y + 4, DialogWidth - 440, 25));
            }

            y += 34;

            SingleComposer.AddStaticText(Lang.Get("canevents:gui-invaudit-filter"), font,
                ElementBounds.Fixed(0, y + 4, 60, 25));
            SingleComposer.AddTextInput(ElementBounds.Fixed(62, y, 180, 25),
                text => nameFilter = text, CairoFont.WhiteSmallText(), "filter");

            SingleComposer.AddSwitch(on => { includeEveryone = on; Refresh(); },
                ElementBounds.Fixed(266, y, 25, 25), "everyone", 25, 3);
            SingleComposer.AddStaticText(Lang.Get("canevents:gui-invaudit-everyone"), font,
                ElementBounds.Fixed(298, y + 4, 380, 25));
        }

        private void ComposeFooter()
        {
            double y = ListTop + viewportHeight + 14;

            string status = InventoryAuditCards.StatusLine(networkHandler, out bool warn);
            SingleComposer.AddStaticText(status,
                CairoFont.WhiteSmallText().WithColor(warn ? Palette.Live : Palette.Detail),
                ElementBounds.Fixed(0, y, DialogWidth, 20));

            if (!string.IsNullOrEmpty(lastResult))
                SingleComposer.AddStaticText(lastResult,
                    CairoFont.WhiteSmallText().WithColor(lastResultOk ? Palette.Go : Palette.Stop),
                    ElementBounds.Fixed(0, y + 18, DialogWidth - 150, 40));

            if (mode == Mode.Detail)
            {
                SingleComposer.AddSmallButton(Lang.Get("canevents:gui-invaudit-back"),
                    () =>
                    {
                        mode = Mode.Holders;
                        scrollOffset = 0;
                        Compose();
                        return true;
                    },
                    ElementBounds.Fixed(EnumDialogArea.RightFixed, 0, y - 4, 0, 0).WithFixedPadding(10, 3),
                    EnumButtonStyle.Normal);

                SingleComposer.AddSmallButton(Lang.Get("canevents:gui-invaudit-history-player"),
                    () =>
                    {
                        historyUid = detailUid;
                        historyName = networkHandler.InventoryBackupDetail.Value?.PlayerName;
                        mode = Mode.History;
                        scrollOffset = 0;
                        Refresh();
                        Compose();
                        return true;
                    },
                    ElementBounds.Fixed(EnumDialogArea.RightFixed, 0, y - 4, 0, 0)
                        .WithFixedPadding(10, 3).WithFixedAlignmentOffset(-150, 0),
                    EnumButtonStyle.Normal);
            }
        }

        private void BuildCards()
        {
            cards.Clear();
            switch (mode)
            {
                case Mode.History:
                    cards.AddRange(InventoryAuditCards.History(networkHandler));
                    break;
                case Mode.Detail:
                    cards.AddRange(InventoryAuditCards.Detail(capi, networkHandler, detailUid, detailParked,
                        confirmingUid, OpenSnapshot, HandBack));
                    break;
                default:
                    cards.AddRange(InventoryAuditCards.Holders(networkHandler, OpenSnapshot));
                    break;
            }
        }

        private void OpenSnapshot(string uid, bool parked)
        {
            confirmingUid = null;
            networkHandler.RequestInventoryBackupDetail(uid, parked);
        }

        private void HandBack(string uid)
        {
            if (confirmingUid == uid)
            {
                networkHandler.SendRestoreParkedBackup(uid);
                confirmingUid = null;
                mode = Mode.Holders;
            }
            else confirmingUid = uid;
            Compose();
        }

        // List plumbing mirrors EventBrowserGui.Layout.

        private void ComposeCards()
        {
            var listBounds = ElementBounds.Fixed(0, ListTop, DialogWidth, viewportHeight);
            var insetBounds = ElementBounds.Fixed(-6, ListTop - 6, DialogWidth + 12, viewportHeight + 12);
            clipBounds = listBounds.ForkBoundingParent();
            var scrollbarBounds = ElementBounds.Fixed(
                listBounds.fixedWidth + ScrollbarGap, ListTop, ScrollbarWidth, viewportHeight);

            var rows = cards.Select(EventCardRow.Single).ToArray();

            SingleComposer
                .AddInset(insetBounds, 4)
                .BeginClip(clipBounds)
                .AddCellList(listBounds, (row, bounds) => new EventTileRow(capi, row, bounds), rows, "cardlist")
                .EndClip()
                .AddInteractiveElement(new SlimScrollbar(capi, OnNewScrollbarValue, scrollbarBounds), "scrollbar");

            var list = SingleComposer.GetCellList<EventCardRow>("cardlist");
            list.unscaledCellSpacing = 6;
            list.UnscaledCellVerPadding = 0;
            list.UnscaledCellHorPadding = 4;
        }

        // True if the height changed and the dialog needs another layout pass.
        private bool AdjustViewport()
        {
            var list = SingleComposer?.GetCellList<EventCardRow>("cardlist");
            if (list == null) return false;

            double content = 0;
            foreach (var cell in list.elementCells)
                content += cell.Bounds.fixedHeight + list.unscaledCellSpacing;
            content = Math.Max(0, content - list.unscaledCellSpacing);

            double wanted = Math.Clamp(content, 90, MaxListHeight);
            if (Math.Abs(wanted - viewportHeight) < 0.5) return false;
            viewportHeight = wanted;
            return true;
        }

        private void UpdateScrollbar()
        {
            var list = SingleComposer?.GetCellList<EventCardRow>("cardlist");
            var scrollbar = SingleComposer?.GetScrollbar("scrollbar");
            if (list == null || scrollbar == null || clipBounds == null) return;

            // Saved before SetHeights, which fires the changed callback with 0 on a fresh scrollbar.
            float wanted = scrollOffset;

            scrollbar.Bounds.CalcWorldBounds();
            scrollbar.SetHeights((float)clipBounds.fixedHeight, (float)list.Bounds.fixedHeight);

            float maxOffset = (float)Math.Max(0, list.Bounds.fixedHeight - clipBounds.fixedHeight);
            wanted = Math.Min(wanted, maxOffset);

            scrollOffset = wanted;
            scrollbar.CurrentYPosition = wanted;
            OnNewScrollbarValue(wanted);

            if (scrollbar is SlimScrollbar slim) slim.Visible = maxOffset > 0.5;
        }

        private void OnNewScrollbarValue(float value)
        {
            scrollOffset = value;
            var list = SingleComposer?.GetCellList<EventCardRow>("cardlist");
            if (list == null) return;
            // Relative to the clip: ForkBoundingParent moved the list's fixed coords to 0,0.
            list.Bounds.fixedY = -value;
            list.Bounds.CalcWorldBounds();
        }
    }
}
