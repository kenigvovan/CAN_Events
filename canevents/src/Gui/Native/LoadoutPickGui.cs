using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace canevents.src.Gui.Native
{
    // Class picker for queue join, tournament, and match start (server request with a countdown).
    public class LoadoutPickGui : GuiDialog
    {
        private readonly ClientNetworkHandler networkHandler;

        // Queue-time mode.
        private EventListEntryMsg queueEvent;

        // Match-start mode.
        private LoadoutPickRequestMsg pickRequest;
        private readonly CountdownClock pickClock;
        private bool alwaysUse;

        private string[] setNames = new string[0];
        private List<ItemStack>[] setStacks = new List<ItemStack>[0];
        private int selected;

        private const double CardWidth = 470;

        // Card list height from the previous compose; 260 is a first-pass guess.
        private double measuredListHeight = 260;

        private bool measuring;

        public LoadoutPickGui(ICoreClientAPI capi, ClientNetworkHandler networkHandler) : base(capi)
        {
            this.networkHandler = networkHandler;
            pickClock = new CountdownClock(capi);
        }

        public override string ToggleKeyCombinationCode => null;

        public void OpenForQueue(EventListEntryMsg ev)
        {
            queueEvent = ev;
            pickRequest = null;
            selected = 0;
            setNames = new string[ev.LoadoutSets.Length];
            setStacks = new List<ItemStack>[ev.LoadoutSets.Length];
            for (int i = 0; i < ev.LoadoutSets.Length; i++)
            {
                setNames[i] = ev.LoadoutSets[i].Name;
                setStacks[i] = GuiItemUtil.ResolvePacked(capi, ev.LoadoutSets[i].ItemCodes);
            }
            Compose();
            TryOpen();
        }

        // allowed empty means all classes.
        public void OpenForTournament(EventListEntryMsg ev, long tournamentId, List<string> allowed)
        {
            if (allowed != null && allowed.Count > 0 && ev?.LoadoutSets != null)
            {
                var filtered = ev.LoadoutSets.Where(s => allowed.Contains(s.Name)).ToArray();
                if (filtered.Length > 0)
                {
                    // Copy so the browser's entry keeps every class.
                    ev = new EventListEntryMsg
                    {
                        GameModeCode = ev.GameModeCode,
                        ArenaName = ev.ArenaName,
                        LoadoutSets = filtered
                    };
                }
            }

            OpenForQueue(ev);
            tournamentPickId = tournamentId;
        }

        private long tournamentPickId;

        public void OpenForMatchStart(LoadoutPickRequestMsg msg)
        {
            if (msg?.Sets == null || msg.Sets.Length == 0) return;

            queueEvent = null;
            pickRequest = msg;
            pickClock.Restart(msg.TimeoutSeconds);
            alwaysUse = !string.IsNullOrEmpty(msg.CurrentDefault);

            setNames = new string[msg.Sets.Length];
            setStacks = new List<ItemStack>[msg.Sets.Length];
            selected = 0;
            string preselect = msg.Preselected ?? msg.CurrentDefault;
            ClientTrace.Step("LoadoutPickGui resolve " + msg.Sets.Length + " sets", () =>
            {
                for (int i = 0; i < msg.Sets.Length; i++)
                {
                    setNames[i] = msg.Sets[i].Name;
                    setStacks[i] = GuiItemUtil.ResolvePacked(capi, msg.Sets[i].ItemCodes);
                    if (setNames[i] == preselect) selected = i;
                }
            });

            ClientTrace.Step("LoadoutPickGui open (match start)", () => { Compose(); TryOpen(); });
        }

        private void Compose()
        {
            string eventCode = (queueEvent?.GameModeCode ?? pickRequest?.EventCode ?? "").ToUpperInvariant();

            var composer = DialogShell.Begin(capi, "canevents-loadoutpick",
                Lang.Get("canevents:gui-pick-title", eventCode), OnClose);

            double y = DialogShell.BodyTop;

            if (pickRequest != null && pickRequest.TimeoutSeconds > 0f)
            {
                composer.AddDynamicText(CountdownText(), CairoFont.WhiteSmallishText(),
                    ElementBounds.Fixed(0, y, CardWidth, 25), "countdown");
                y += 30;
            }

            if (!string.IsNullOrEmpty(queueEvent?.EntryFeeItems))
            {
                composer.AddStaticText(
                    Lang.Get("canevents:gui-fee") + ": " + GuiItemUtil.DescribePacked(capi, queueEvent.EntryFeeItems),
                    CairoFont.WhiteDetailText().WithColor(GuiStyle.ErrorTextColor),
                    ElementBounds.Fixed(0, y, CardWidth, 25));
                y += 30;
            }

            // One card per row; item icon lists need the full width.
            var cards = new EventCardRow[setNames.Length];
            for (int i = 0; i < setNames.Length; i++)
            {
                int index = i;
                bool isSelected = selected == i;
                cards[i] = EventCardRow.Single(new EventCard
                {
                    Title = setNames[i],
                    Icons = setStacks[i],
                    BadgeText = isSelected ? Lang.Get("canevents:gui-selected") : null,
                    Accent = isSelected ? Palette.Go : Palette.Title,
                    OnClick = () => { selected = index; Compose(); }
                });
            }

            // No clip: dialog grows to fit. The cell list measures itself only during Compose,
            // so use the previous pass's height and recompose once (see MeasureList).
            var listBounds = ElementBounds.Fixed(0, y, CardWidth, measuredListHeight);

            composer.AddCellList(listBounds,
                (row, bounds) => new EventTileRow(capi, row, bounds), cards, "sets");

            // Padding must be set before Compose(), when the list builds its cell bounds.
            var setList = composer.GetCellList<EventCardRow>("sets");
            setList.unscaledCellSpacing = 6;
            setList.UnscaledCellVerPadding = 0;
            setList.UnscaledCellHorPadding = 4;

            y += measuredListHeight + 12;

            if (pickRequest != null)
            {
                composer
                    .AddStaticText(Lang.Get("canevents:gui-always-use", (pickRequest.EventCode ?? "").ToUpperInvariant()),
                        CairoFont.WhiteDetailText(), ElementBounds.Fixed(40, y + 3, 380, 25))
                    .AddSwitch(on => alwaysUse = on, ElementBounds.Fixed(0, y, 30, 25), "alwaysuse");
                y += 34;
            }

            composer.AddSmallButton(Lang.Get("canevents:gui-confirm"), OnConfirm,
                ElementBounds.Fixed(EnumDialogArea.LeftFixed, 0, y, 0, 0).WithFixedPadding(10, 4),
                EnumButtonStyle.Normal);
            composer.AddSmallButton(Lang.Get("canevents:gui-cancel"), () => { OnClose(); return true; },
                ElementBounds.Fixed(EnumDialogArea.LeftFixed, 140, y, 0, 0).WithFixedPadding(10, 4),
                EnumButtonStyle.Normal);

            string shopEvent = queueEvent?.GameModeCode ?? pickRequest?.EventCode;
            if (ShopPreview != null && HasShop(shopEvent))
            {
                composer.AddSmallButton(Lang.Get("canevents:gui-pick-shop"), () =>
                {
                    ShopPreview.Show(shopEvent, setNames.Length > selected ? setNames[selected] : null);
                    return true;
                }, ElementBounds.Fixed(EnumDialogArea.LeftFixed, 280, y, 0, 0).WithFixedPadding(10, 4),
                EnumButtonStyle.Small);
            }

            SingleComposer = composer.EndChildElements().Compose();

            // Compose() reruns on every selection change.
            if (pickRequest != null) SingleComposer.GetSwitch("alwaysuse")?.SetValue(alwaysUse);

            // Second pass measures the same cards, so this settles after one recompose.
            if (!measuring && MeasureList())
            {
                measuring = true;
                Compose();
                measuring = false;
            }
        }

        // True when the real height differs and the dialog needs another compose.
        private bool MeasureList()
        {
            var list = SingleComposer?.GetCellList<EventCardRow>("sets");
            if (list == null) return false;

            double actual = list.Bounds.fixedHeight;
            if (actual <= 0 || Math.Abs(actual - measuredListHeight) < 0.5) return false;

            measuredListHeight = actual;
            return true;
        }

        private bool OnConfirm()
        {
            if (setNames.Length == 0) { OnClose(); return true; }

            // State is cleared BEFORE TryClose so OnGuiClosed can tell a confirm from a dismissal.
            if (pickRequest != null)
            {
                networkHandler.SendLoadoutPickResponse(pickRequest.EventId, pickRequest.EventCode,
                    setNames[selected], alwaysUse, pickRequest.RegistrationGuid);
                pickRequest = null;
            }
            else if (tournamentPickId > 0)
            {
                networkHandler.SendTournamentLoadout(tournamentPickId, setNames[selected]);
                tournamentPickId = 0;
                queueEvent = null;
            }
            else if (queueEvent != null)
            {
                networkHandler.SendJoinRequest(queueEvent.GameModeCode, setNames[selected]);
                networkHandler.RequestEventList();
                queueEvent = null;
            }

            OnClose();
            return true;
        }

        private void OnClose() => TryClose();

        public ShopPreviewGui ShopPreview;

        // The match-start request carries no shop flag; the lobby entry of the same event does.
        private bool HasShop(string eventCode)
            => queueEvent?.HasShop == true
               || networkHandler.EventList.Value?.Events?.Any(e => e.EventId == 0 && e.GameModeCode == eventCode && e.HasShop) == true;

        // Reopens the browser after a dismissed queue pick.
        public Action OnQueuePickCancelled;

        // Layout composed before TryOpen is not always the one the open dialog ends up with.
        public override void OnGuiOpened()
        {
            base.OnGuiOpened();
            Compose();
        }

        // Every exit path ends here; Escape never calls OnClose.
        public override void OnGuiClosed()
        {
            base.OnGuiClosed();

            // A dismissed tournament pick changes nothing and must not reopen the browser.
            bool wasQueuePick = queueEvent != null && tournamentPickId == 0;
            tournamentPickId = 0;
            pickRequest = null;
            queueEvent = null;
            if (wasQueuePick) OnQueuePickCancelled?.Invoke();
        }

        public override void OnRenderGUI(float deltaTime)
        {
            base.OnRenderGUI(deltaTime);
            if (pickRequest == null || pickRequest.TimeoutSeconds <= 0f) return;

            // Server already started with the default; CountdownClock leaves a small margin.
            if (pickClock.Expired) { OnClose(); return; }

            SingleComposer?.GetDynamicText("countdown")?.SetNewText(CountdownText());
        }

        private string CountdownText()
            => Lang.Get(pickRequest?.CountdownLangKey ?? "canevents:gui-match-starts-in", pickClock.SecondsLeft);
    }
}
