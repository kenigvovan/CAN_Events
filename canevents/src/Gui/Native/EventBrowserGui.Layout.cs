using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace canevents.src.Gui.Native
{
    // Shared scrollable card list; tabs add cards and call ComposeCards.
    public partial class EventBrowserGui
    {
        protected const double DialogWidth = 660;

        // Children start 20px down and the title bar is 31px, so with the 6px inset grow this lands 3px under the bar.
        protected const double ListTop = 20;

        // Cap only; the real height is snapped to rows in AdjustViewport.
        protected const double MaxListHeight = 420;
        private double viewportHeight = MaxListHeight;

        private const int TilesPerRow = 2;
        private const double ScrollbarWidth = 12;
        private const double ScrollbarGap = 10;   // from the list's right edge, i.e. 4px off the inset

        private float scrollOffset;
        private ElementBounds clipBounds;
        private readonly List<EventCard> cards = new List<EventCard>();

        protected void AddCard(string title, string detail = null, string badge = null,
                               double[] accent = null, Action onClick = null,
                               double? progress = null, List<ItemStack> icons = null,
                               string titleIcon = null)
            => cards.Add(new EventCard
            {
                Title = title,
                Detail = detail,
                BadgeText = badge,
                Accent = accent,
                OnClick = onClick,
                Progress = progress,
                Icons = icons,
                TitleIcon = titleIcon
            });

        protected void AddHeader(string title)
            => cards.Add(new EventCard { Title = title, IsHeader = true });

        protected void ComposeCards()
        {
            var listBounds = ElementBounds.Fixed(0, ListTop, DialogWidth, viewportHeight);
            // Not copied from listBounds: ForkBoundingParent moves the list to 0,0 inside the clip.
            var insetBounds = ElementBounds.Fixed(-6, ListTop - 6, DialogWidth + 12, viewportHeight + 12);
            clipBounds = listBounds.ForkBoundingParent();
            // Clip height, not inset height.
            var scrollbarBounds = ElementBounds.Fixed(
                listBounds.fixedWidth + ScrollbarGap, ListTop, ScrollbarWidth, viewportHeight);

            // Snapshot: the cell creator runs later, after `cards` may be refilled.
            var snapshot = BuildRows();

            SingleComposer
                .AddInset(insetBounds, 4)
                .BeginClip(clipBounds)
                .AddCellList(listBounds, CreateCell, snapshot, "cardlist")
                .EndClip()
                .AddInteractiveElement(new SlimScrollbar(capi, OnNewScrollbarValue, scrollbarBounds), "scrollbar");

            // Must be set before Compose(), which bakes them into cell bounds.
            var list = SingleComposer.GetCellList<EventCardRow>("cardlist");
            list.unscaledCellSpacing = 6;
            list.UnscaledCellVerPadding = 0;
            list.UnscaledCellHorPadding = 4;

            cards.Clear();
        }

        // For rosters and leaderboards.
        protected bool SingleColumnCards;

        private EventCardRow[] BuildRows()
        {
            var rows = new List<EventCardRow>();
            var pending = new List<EventCard>(2);
            int perRow = SingleColumnCards ? 1 : TilesPerRow;

            void Flush()
            {
                if (pending.Count == 0) return;
                rows.Add(new EventCardRow { Tiles = pending.ToArray(), FullWidth = SingleColumnCards });
                pending.Clear();
            }

            foreach (var card in cards)
            {
                if (card.IsHeader)
                {
                    Flush();
                    rows.Add(new EventCardRow { Tiles = new[] { card } });
                    continue;
                }

                pending.Add(card);
                if (pending.Count == perRow) Flush();
            }

            Flush();   // a trailing odd tile keeps its half width rather than stretching
            return rows.ToArray();
        }

        private IGuiElementCell CreateCell(EventCardRow row, ElementBounds bounds)
            => new EventTileRow(capi, row, bounds);

        protected double ControlsY => ListTop + viewportHeight + 16;

        // Raised by the bracket tab.
        protected double MinListHeight = 80;

        // Snaps the viewport to a row boundary. True means recompose once.
        protected bool AdjustViewport()
        {
            var list = SingleComposer?.GetCellList<EventCardRow>("cardlist");
            // No card list (bracket tab): just apply the floor.
            if (list == null)
            {
                double floor = Math.Min(MinListHeight, MaxListHeight);
                if (Math.Abs(floor - viewportHeight) < 0.5) return false;
                viewportHeight = floor;
                return true;
            }

            var boundaries = new List<double>();
            double y = 0;
            foreach (var cell in list.elementCells)
            {
                double bottom = y + cell.Bounds.fixedHeight;
                boundaries.Add(bottom);
                y = bottom + list.unscaledCellSpacing;
            }

            double content = boundaries.Count > 0 ? boundaries[boundaries.Count - 1] : 0;
            double wanted;

            if (content <= MaxListHeight)
            {
                wanted = content;                                   // everything fits
            }
            else
            {
                wanted = MaxListHeight;
                foreach (double b in boundaries)
                {
                    if (b > MaxListHeight) break;
                    wanted = b;
                }
            }

            // Grow to a row boundary, not a flat number, so no sliver of the next card shows.
            if (wanted < MinListHeight)
            {
                wanted = MinListHeight;
                foreach (double b in boundaries)
                {
                    if (b < MinListHeight) continue;
                    if (b <= MaxListHeight) wanted = b;
                    break;
                }
            }

            if (boundaries.Count == 0) wanted = MinListHeight;       // empty list
            wanted = Math.Min(wanted, MaxListHeight);

            if (Math.Abs(wanted - viewportHeight) < 0.5) return false;
            viewportHeight = wanted;
            return true;
        }

        protected static ElementBounds ButtonAt(double x, double y)
            => ElementBounds.Fixed(EnumDialogArea.LeftFixed, x, y, 0, 0).WithFixedPadding(9, 4);

        protected void ComposeRefreshButton(Action onRefresh)
            => SingleComposer.AddSmallButton(Vintagestory.API.Config.Lang.Get("canevents:gui-refresh"),
                () => { onRefresh(); return true; }, ButtonAt(0, ControlsY), EnumButtonStyle.Normal);

        // Dynamic text, not on the card: cards are baked into textures and would freeze the clock.
        protected void ComposeSignupNotice()
        {
            string text = SignupNoticeText();
            if (text == null) return;

            lastSignupNotice = text;
            SingleComposer.AddDynamicText(text,
                CairoFont.WhiteSmallText().WithColor(Palette.Live),
                ElementBounds.Fixed(90, ControlsY + 3, DialogWidth - 100, 20), "signupnotice");
        }

        // SetNewText redraws the texture, so only write on change.
        protected void UpdateSignupNotice()
        {
            string text = SignupNoticeText();
            if (text == null || text == lastSignupNotice) return;
            SingleComposer?.GetDynamicText("signupnotice")?.SetNewText(text);
            lastSignupNotice = text;
        }

        private string lastSignupNotice;

        // Dynamic text for the same reason as the sign-up notice.
        protected void ComposeQueueNotice()
        {
            string text = QueueNoticeText();
            if (text == null) return;

            lastQueueNotice = text;
            SingleComposer.AddDynamicText(text,
                CairoFont.WhiteSmallText().WithColor(Palette.Info),
                ElementBounds.Fixed(90, ControlsY + 19, DialogWidth - 100, 20), "queuenotice");
        }

        protected void UpdateQueueNotice()
        {
            string text = QueueNoticeText();
            if (text == null || text == lastQueueNotice) return;
            SingleComposer?.GetDynamicText("queuenotice")?.SetNewText(text);
            lastQueueNotice = text;
        }

        private string lastQueueNotice;

        private string QueueNoticeText()
        {
            var queued = networkHandler.QueuedEntry();
            if (queued == null) return null;

            // The list is only sent on change, so add local time since it arrived.
            double waited = queued.QueuedSeconds + networkHandler.SecondsSinceEventList();

            return Vintagestory.API.Config.Lang.Get("canevents:gui-queue-notice",
                (queued.GameModeCode ?? "").ToUpperInvariant(), TimeText.Short((float)waited));
        }

        private string SignupNoticeText()
        {
            var invite = networkHandler.SoonestInvite();
            if (invite == null) return null;

            string code = (invite.EventCode ?? "").ToUpperInvariant();
            float left = networkHandler.SecondsLeftFor(invite.EventCode);
            return left < 0f
                ? Vintagestory.API.Config.Lang.Get("canevents:gui-signup-notice-manual", code)
                : Vintagestory.API.Config.Lang.Get("canevents:gui-signup-notice", code, TimeText.Short(left));
        }

        private void UpdateScrollbar()
        {
            var list = SingleComposer?.GetCellList<EventCardRow>("cardlist");
            var scrollbar = SingleComposer?.GetScrollbar("scrollbar");
            if (list == null || scrollbar == null || clipBounds == null) return;

            // Save first: SetHeights fires TriggerChanged at 0, which overwrites scrollOffset.
            float wanted = scrollOffset;

            scrollbar.Bounds.CalcWorldBounds();
            scrollbar.SetHeights((float)clipBounds.fixedHeight, (float)list.Bounds.fixedHeight);

            // The list may have gotten shorter.
            float maxOffset = (float)Math.Max(0, list.Bounds.fixedHeight - clipBounds.fixedHeight);
            wanted = Math.Min(wanted, maxOffset);

            scrollOffset = wanted;
            scrollbar.CurrentYPosition = wanted;
            OnNewScrollbarValue(wanted);

            // Same threshold the base class uses to ignore input.
            if (scrollbar is SlimScrollbar slim) slim.Visible = maxOffset > 0.5;
        }

        private void OnNewScrollbarValue(float value)
        {
            scrollOffset = value;
            var list = SingleComposer?.GetCellList<EventCardRow>("cardlist");
            if (list == null) return;
            list.Bounds.fixedY = ListTop - value;
            list.Bounds.CalcWorldBounds();
        }
    }
}
