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

        private const double CardWidth = 470;

        /// <summary>
        /// Height the card list turned out to be, carried from one composition to the next. Starts at
        /// a guess because nothing is measured yet; the first pass corrects it, and everything below
        /// the list is positioned from it.
        /// </summary>
        private double measuredListHeight = 260;

        // Guards the single recompose the measurement triggers, so it cannot recurse.
        private bool measuring;

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

            // No clip and no scrollbar: the list shows every class and the dialog grows to fit.
            //
            // It used to be a fixed-height viewport, and that is what cut the last class in half -
            // the clip edge fell wherever the constant put it, regardless of where cards ended. Three
            // attempts to compute a better constant failed, because the height a card needs is not
            // knowable before it is composed, and the snap-to-boundary trick copied from the browser
            // turns out to be dead there too (it pins MinListHeight to MaxListHeight, so its own
            // adjustment never fires).
            //
            // A class list is three to six entries. Sizing the dialog to them costs a taller window
            // in the rare case of many classes; the alternative cost an invisible one every time.
            // Height from the previous pass. FixedUnder and every y below read fixedHeight at the
            // moment they are called, and the cell list only measures itself later, during Compose -
            // so on the first pass everything under the list stacks on top of it. The measurement is
            // taken afterwards and the dialog is composed once more with it (see MeasureList).
            var listBounds = ElementBounds.Fixed(0, y, CardWidth, measuredListHeight);

            composer.AddCellList(listBounds,
                (row, bounds) => new EventTileRow(capi, row, bounds), cards, "sets");

            // Cell padding has to be set before Compose(), when the list builds its cell bounds.
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

            SingleComposer = composer.EndChildElements().Compose();

            // Restore the switch state - Compose() runs again on every selection change.
            if (pickRequest != null) SingleComposer.GetSwitch("alwaysuse")?.SetValue(alwaysUse);

            // Now that the cards have been measured, lay the dialog out again around their real
            // height. Guarded, and settles immediately: the second pass measures the same cards.
            if (!measuring && MeasureList())
            {
                measuring = true;
                Compose();
                measuring = false;
            }
        }

        /// <summary>
        /// Records how tall the composed card list actually is. True when that differs from what this
        /// pass was built with, meaning everything below the list is in the wrong place and the
        /// dialog has to be composed again.
        /// </summary>
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

        /// <summary>Raised when a queue-time pick is dismissed without confirming. Wired to reopen
        /// the browser, which stepped aside for this dialog.</summary>
        public Action OnQueuePickCancelled;

        /// <summary>
        /// Re-runs the layout once the dialog is actually open. Compose() is called from the Open*
        /// methods, before TryOpen, and a layout built then is not necessarily the one the open
        /// dialog ends up with.
        /// </summary>
        public override void OnGuiOpened()
        {
            base.OnGuiOpened();
            Compose();
        }

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
