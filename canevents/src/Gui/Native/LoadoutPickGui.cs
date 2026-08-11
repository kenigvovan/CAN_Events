using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace canevents.src.Gui.Native
{
    /// <summary>
    /// "Choose your class". One dialog, several entry points - they differ only in where the answer
    /// is sent:
    ///
    /// - Queue-time: confirming sends the join request with the chosen set.
    /// - Match-start: the server asks (LoadoutPickRequestMsg) with a countdown; when it runs out the
    ///   match starts with the default, so the dialog closes itself rather than accept a late confirm.
    /// </summary>
    public class LoadoutPickGui : GuiDialog
    {
        private readonly ClientNetworkHandler networkHandler;

        // Queue-time mode.
        private EventListEntryMsg queueEvent;

        // Match-start mode.
        private LoadoutPickRequestMsg pickRequest;
        private double pickShownAt;
        private bool alwaysUse;

        private string[] setNames = new string[0];
        private List<ItemStack>[] setStacks = new List<ItemStack>[0];
        private int selected;

        // Sized so three cards fit: picking a class should not mean scrolling to see the options.
        private const double CardWidth = 470;
        private const double ListViewportHeight = 280;

        private ElementBounds clipBounds;
        private double listTop;
        private float setsScroll;

        public LoadoutPickGui(ICoreClientAPI capi, ClientNetworkHandler networkHandler) : base(capi)
        {
            this.networkHandler = networkHandler;
        }

        // Opened programmatically from the browser or by a server packet, never by a key.
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

        /// <summary>
        /// Tournament entry point: same class list as the queue, but confirming stores the pick
        /// against the tournament - a bracket is several matches and the choice is made once.
        /// </summary>
        /// <param name="allowed">
        /// Classes the organiser permits; empty means all. Filtered here rather than rejected after
        /// the pick.
        /// </param>
        public void OpenForTournament(EventListEntryMsg ev, long tournamentId, List<string> allowed)
        {
            if (allowed != null && allowed.Count > 0 && ev?.LoadoutSets != null)
            {
                var filtered = ev.LoadoutSets.Where(s => allowed.Contains(s.Name)).ToArray();
                if (filtered.Length > 0)
                {
                    // A shallow copy: the browser's own list must keep every class for the queue.
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
            pickShownAt = capi.ElapsedMilliseconds / 1000.0;
            // Pre-tick when a default was used before - already what the player asked for.
            alwaysUse = !string.IsNullOrEmpty(msg.CurrentDefault);

            setNames = new string[msg.Sets.Length];
            setStacks = new List<ItemStack>[msg.Sets.Length];
            selected = 0;
            for (int i = 0; i < msg.Sets.Length; i++)
            {
                setNames[i] = msg.Sets[i].Name;
                setStacks[i] = GuiItemUtil.ResolvePacked(capi, msg.Sets[i].ItemCodes);
                if (setNames[i] == msg.CurrentDefault) selected = i;
            }

            Compose();
            TryOpen();
        }

        private void Compose()
        {
            string eventCode = (queueEvent?.GameModeCode ?? pickRequest?.EventCode ?? "").ToUpperInvariant();

            var bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
            bgBounds.BothSizing = ElementSizing.FitToChildren;
            var dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);

            var composer = capi.Gui
                .CreateCompo("canevents-loadoutpick", dialogBounds)
                .AddShadedDialogBG(bgBounds)
                .AddDialogTitleBar(Lang.Get("canevents:gui-pick-title", eventCode), OnClose)
                .BeginChildElements(bgBounds);

            // bgBounds children start 20px below the dialog top but the title bar is 31px tall, so at
            // y=0 the first line sits on top of it.
            double y = 14;

            if (pickRequest != null && pickRequest.TimeoutSeconds > 0f)
            {
                composer.AddDynamicText(CountdownText(), CairoFont.WhiteSmallishText(),
                    ElementBounds.Fixed(0, y, CardWidth, 25), "countdown");
                y += 30;
            }

            // The fee is charged at match start, so it belongs next to the decision to join.
            if (!string.IsNullOrEmpty(queueEvent?.EntryFeeItems))
            {
                composer.AddStaticText(
                    Lang.Get("canevents:gui-fee") + ": " + GuiItemUtil.DescribePacked(capi, queueEvent.EntryFeeItems),
                    CairoFont.WhiteDetailText().WithColor(GuiStyle.ErrorTextColor),
                    ElementBounds.Fixed(0, y, CardWidth, 25));
                y += 30;
            }

            // Classes as cards, same look as the event list; the chosen one is marked by accent and
            // badge. The kit is a row of item icons with the inventory tooltip on hover - spelled-out
            // names wrapped onto four lines and still got cut. One card per row: the item lists need
            // the full width.
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

            // Fixed viewport with a scrollbar, not a box sized to a guessed row height: cards vary in
            // height with how far their item list wraps.
            listTop = y;
            var listBounds = ElementBounds.Fixed(0, y, CardWidth, ListViewportHeight);
            // Fixed coords of its own, not a copy of listBounds: ForkBoundingParent rearranges the
            // child it takes, so a copy made after the fork starts at y=0 instead of the list's top.
            var scrollbarBounds = ElementBounds.Fixed(
                CardWidth + 12, y, 20, ListViewportHeight + 4);
            clipBounds = listBounds.ForkBoundingParent();

            composer
                .BeginClip(clipBounds)
                .AddCellList(listBounds, (row, bounds) => new EventTileRow(capi, row, bounds), cards, "sets")
                .EndClip()
                .AddVerticalScrollbar(OnSetsScroll, scrollbarBounds, "setscroll");

            // Cell padding has to be set before Compose(), when the list builds its cell bounds.
            var setList = composer.GetCellList<EventCardRow>("sets");
            setList.unscaledCellSpacing = 6;
            setList.UnscaledCellVerPadding = 0;
            setList.UnscaledCellHorPadding = 4;

            y += ListViewportHeight + 12;

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

            SingleComposer = composer.EndChildElements().Compose();

            // Restore the switch state - Compose() runs again on every selection change.
            if (pickRequest != null) SingleComposer.GetSwitch("alwaysuse")?.SetValue(alwaysUse);

            SetupSetsScroll();
        }

        // As in the browser's list: SetHeights fires the change callback on a fresh scrollbar sitting
        // at zero, so the wanted offset has to be saved across it or every recompose scrolls to the top.
        private void SetupSetsScroll()
        {
            var list = SingleComposer?.GetCellList<EventCardRow>("sets");
            var scrollbar = SingleComposer?.GetScrollbar("setscroll");
            if (list == null || scrollbar == null || clipBounds == null) return;

            float wanted = setsScroll;
            scrollbar.Bounds.CalcWorldBounds();
            scrollbar.SetHeights((float)ListViewportHeight, (float)list.Bounds.fixedHeight);

            wanted = Math.Min(wanted, (float)Math.Max(0, list.Bounds.fixedHeight - ListViewportHeight));
            setsScroll = wanted;
            scrollbar.CurrentYPosition = wanted;
            OnSetsScroll(wanted);
        }

        private void OnSetsScroll(float value)
        {
            setsScroll = value;
            var list = SingleComposer?.GetCellList<EventCardRow>("sets");
            if (list == null) return;
            list.Bounds.fixedY = listTop - value;
            list.Bounds.CalcWorldBounds();
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

        /// <summary>Raised when a queue-time pick is dismissed without confirming. Wired to reopen
        /// the browser, which stepped aside for this dialog.</summary>
        public Action OnQueuePickCancelled;

        // The one funnel every way out goes through (Cancel, the cross, Escape, the countdown), which
        // is why dismissal logic lives here and not in OnClose - Escape never calls OnClose.
        public override void OnGuiClosed()
        {
            base.OnGuiClosed();

            // Dismissing a match-start pick means "start me with the default"; otherwise the request
            // is re-offered on the next packet.
            if (pickRequest != null) networkHandler.DismissLoadoutPick();

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

            // Past the server-side window the match has started with the default, so close instead of
            // leaving a mouse-grabbing dialog at "0s". The margin lets a last-frame confirm land.
            if (Remaining() <= -0.5f) { OnClose(); return; }

            SingleComposer?.GetDynamicText("countdown")?.SetNewText(CountdownText());
        }

        private float Remaining()
            => pickRequest.TimeoutSeconds - (float)(capi.ElapsedMilliseconds / 1000.0 - pickShownAt);

        private string CountdownText()
            => Lang.Get("canevents:gui-match-starts-in", Math.Max(0, (int)Remaining()));
    }
}
