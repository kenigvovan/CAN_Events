using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace canevents.src.Gui.Native
{
    // Admin-only inventory audit tab; cards come from InventoryAuditCards.
    public partial class EventBrowserGui
    {
        // Last in the strip so existing tab indices don't shift.
        private const int InventoryTab = 7;

        // Cosmetic only; the server re-checks every audit packet.
        private bool MayAuditInventories
            => capi.World?.Player?.HasPrivilege(Privilege.controlserver) ?? false;

        // Null shows the list.
        private string invDetailUid;
        private bool invDetailParked;

        // First click of a hand-back arms it, second sends it.
        private string invConfirmingUid;

        private bool invShowHistory;
        private bool invIncludeEveryone;

        private string invNameFilter = "";

        // Null shows everyone's history.
        private string invHistoryUid;
        private string invHistoryName;

        private void RequestInventoryAudit()
            => networkHandler.RequestInventoryAudit(
                string.IsNullOrWhiteSpace(invNameFilter) ? null : invNameFilter,
                invIncludeEveryone,
                historyLimit: 150,
                historyUid: invHistoryUid);

        private void ComposeInventoryTab()
        {
            if (!MayAuditInventories)
            {
                AddCard(Lang.Get("canevents:gui-invaudit-title"),
                    Lang.Get("canevents:gui-invaudit-no-privilege"), accent: Palette.Idle);
                ComposeCards();
                ComposeRefreshButton(RequestInventoryAudit);
                return;
            }

            if (invDetailUid != null)
            {
                foreach (var card in InventoryAuditCards.Detail(capi, networkHandler, invDetailUid,
                             invDetailParked, invConfirmingUid, OpenInventorySnapshot, HandBackInventory))
                    cards.Add(card);
            }
            else if (invShowHistory)
            {
                foreach (var card in InventoryAuditCards.History(networkHandler)) cards.Add(card);
            }
            else
            {
                foreach (var card in InventoryAuditCards.Holders(networkHandler, OpenInventorySnapshot))
                    cards.Add(card);
            }

            ComposeCards();
            ComposeInventoryControls();
        }

        private void ComposeInventoryControls()
        {
            double y = ControlsY;

            if (invDetailUid != null)
            {
                SingleComposer.AddSmallButton(Lang.Get("canevents:gui-invaudit-back"),
                    () => { invDetailUid = null; invConfirmingUid = null; scrollOffset = 0; ComposeDialog(); return true; },
                    ButtonAt(0, y), EnumButtonStyle.Normal);

                SingleComposer.AddSmallButton(Lang.Get("canevents:gui-invaudit-history-player"),
                    () =>
                    {
                        invHistoryUid = invDetailUid;
                        invHistoryName = networkHandler.InventoryBackupDetail.Value?.PlayerName;
                        invDetailUid = null;
                        invShowHistory = true;
                        scrollOffset = 0;
                        RequestInventoryAudit();
                        ComposeDialog();
                        return true;
                    },
                    ButtonAt(150, y), EnumButtonStyle.Normal);
            }
            else
            {
                SingleComposer.AddSmallButton(
                    Lang.Get(invShowHistory ? "canevents:gui-invaudit-holders" : "canevents:gui-invaudit-history"),
                    () =>
                    {
                        invShowHistory = !invShowHistory;
                        scrollOffset = 0;
                        ComposeDialog();
                        return true;
                    },
                    ButtonAt(0, y), EnumButtonStyle.Normal);

                SingleComposer.AddSmallButton(Lang.Get("canevents:gui-refresh"),
                    () => { RequestInventoryAudit(); return true; },
                    ButtonAt(150, y), EnumButtonStyle.Normal);

                if (invShowHistory && invHistoryUid != null)
                {
                    SingleComposer.AddSmallButton(Lang.Get("canevents:gui-invaudit-history-all"),
                        () =>
                        {
                            invHistoryUid = null;
                            invHistoryName = null;
                            scrollOffset = 0;
                            RequestInventoryAudit();
                            ComposeDialog();
                            return true;
                        },
                        ButtonAt(260, y), EnumButtonStyle.Normal);

                    SingleComposer.AddStaticText(
                        Lang.Get("canevents:gui-invaudit-history-of", invHistoryName ?? invHistoryUid),
                        CairoFont.WhiteSmallText().WithColor(Palette.Info),
                        ElementBounds.Fixed(410, y + 5, DialogWidth - 420, 25));
                }
                else if (!invShowHistory)
                {
                    // Server caps how many long-absent accounts one sweep reads; name search reaches the rest.
                    SingleComposer.AddStaticText(Lang.Get("canevents:gui-invaudit-filter"),
                        CairoFont.WhiteSmallText(), ElementBounds.Fixed(262, y + 5, 50, 25));
                    SingleComposer.AddTextInput(ElementBounds.Fixed(306, y, 150, 25),
                        text => invNameFilter = text, CairoFont.WhiteSmallText(), "invFilter");
                    SingleComposer.AddSmallButton(Lang.Get("canevents:gui-invaudit-search"),
                        () => { scrollOffset = 0; RequestInventoryAudit(); return true; },
                        ButtonAt(466, y), EnumButtonStyle.Normal);

                    SingleComposer.AddSwitch(on => { invIncludeEveryone = on; RequestInventoryAudit(); },
                        ElementBounds.Fixed(0, y + 30, 25, 25), "invEveryone", 25, 3);
                    SingleComposer.AddStaticText(Lang.Get("canevents:gui-invaudit-everyone"),
                        CairoFont.WhiteSmallText(), ElementBounds.Fixed(32, y + 34, DialogWidth - 40, 25));
                }
            }

            bool hasToggleRow = invDetailUid == null && !invShowHistory;
            string status = InventoryAuditCards.StatusLine(networkHandler, out bool warn);
            SingleComposer.AddStaticText(status,
                CairoFont.WhiteSmallText().WithColor(warn ? Palette.Live : Palette.Detail),
                ElementBounds.Fixed(0, y + (hasToggleRow ? 62 : 30), DialogWidth, 20));
        }

        // Must run after Compose(): a fresh composer resets every switch to off.
        private void RestoreInventoryTabState()
        {
            if (curTab != InventoryTab || invDetailUid != null || invShowHistory) return;
            SingleComposer?.GetSwitch("invEveryone")?.SetValue(invIncludeEveryone);
            SingleComposer?.GetTextInput("invFilter")?.SetValue(invNameFilter);
        }

        private void OpenInventorySnapshot(string uid, bool parked)
        {
            invDetailUid = uid;
            invDetailParked = parked;
            invConfirmingUid = null;
            scrollOffset = 0;
            networkHandler.RequestInventoryBackupDetail(uid, parked);
            ComposeDialog();
        }

        private void HandBackInventory(string uid)
        {
            if (invConfirmingUid == uid)
            {
                networkHandler.SendRestoreParkedBackup(uid);
                invConfirmingUid = null;
                invDetailUid = null;
                // Server sends a fresh list anyway; ask in case that reply is lost.
                RequestInventoryAudit();
            }
            else invConfirmingUid = uid;

            ComposeDialog();
        }
    }
}
