using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace canevents.src.Gui.Native
{
    /// <summary>
    /// The one scrollable card list every tab draws into: tabs push cards and call
    /// <see cref="ComposeCards"/> instead of hand-rolling clip bounds and a scrollbar each.
    /// </summary>
    public partial class EventBrowserGui
    {
        protected const double DialogWidth = 660;

        // bgBounds children start 20px below the dialog top, the title bar takes the first 31px, so
        // the inset (list top minus its 6px grow) must clear 11px of it. 20 puts it 3px under the bar.
        protected const double ListTop = 20;

        /// <summary>Viewport cap. The actual height is snapped to the rows, see <see cref="AdjustViewport"/>.</summary>
        protected const double MaxListHeight = 420;
        private double viewportHeight = MaxListHeight;

        private const int TilesPerRow = 2;
        private const double ScrollbarWidth = 12;
        private const double ScrollbarGap = 10;   // from the list's right edge, i.e. 4px off the inset

        private float scrollOffset;
        private ElementBounds clipBounds;
        private readonly List<EventCard> cards = new List<EventCard>();

        /// <summary>A normal card. Without an onClick it is informational.</summary>
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

        /// <summary>A section label between groups of cards - flat, no frame.</summary>
        protected void AddHeader(string title)
            => cards.Add(new EventCard { Title = title, IsHeader = true });

        protected void ComposeCards()
        {
            var listBounds = ElementBounds.Fixed(0, ListTop, DialogWidth, viewportHeight);
            clipBounds = listBounds.ForkBoundingParent();
            var insetBounds = listBounds.FlatCopy().FixedGrow(6);
            // Height matches the clip, not the inset, so the handle travels exactly the visible range.
            var scrollbarBounds = ElementBounds.Fixed(
                listBounds.fixedWidth + ScrollbarGap, ListTop, ScrollbarWidth, viewportHeight);

            // Snapshot: the cell creator runs later, while `cards` is refilled by the next recompose.
            var snapshot = BuildRows();

            SingleComposer
                .AddInset(insetBounds, 4)
                .BeginClip(clipBounds)
                .AddCellList(listBounds, CreateCell, snapshot, "cardlist")
                .EndClip()
                .AddInteractiveElement(new SlimScrollbar(capi, OnNewScrollbarValue, scrollbarBounds), "scrollbar");

            // Before Compose(), not after: the list bakes these into every cell's bounds inside
            // Compose, so setting them later leaves the vanilla padding in place.
            var list = SingleComposer.GetCellList<EventCardRow>("cardlist");
            list.unscaledCellSpacing = 6;
            list.UnscaledCellVerPadding = 0;
            list.UnscaledCellHorPadding = 4;

            cards.Clear();
        }

        /// <summary>
        /// One tile per row instead of two. The grid suits event lists; rosters and leaderboards are
        /// lists of rows, where two-up leaves half the window empty on an odd count.
        /// </summary>
        protected bool SingleColumnCards;

        /// <summary>Packs cards into grid rows; a header always takes a row of its own.</summary>
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

        /// <summary>Y of the button row every tab puts under its list. Tracks the snapped viewport.</summary>
        protected double ControlsY => ListTop + viewportHeight + 16;

        /// <summary>Floor for the snapped viewport, raised by tabs whose content is a picture (a bracket).</summary>
        protected double MinListHeight = 80;

        /// <summary>
        /// Snaps the viewport to a row boundary so the inset never cuts a tile in half. True when the
        /// height changed and the caller must recompose - once, since the snap then settles.
        /// </summary>
        protected bool AdjustViewport()
        {
            var list = SingleComposer?.GetCellList<EventCardRow>("cardlist");
            // A tab that draws no card list at all (the bracket) still wants its floor honoured.
            if (list == null)
            {
                double floor = Math.Min(MinListHeight, MaxListHeight);
                if (Math.Abs(floor - viewportHeight) < 0.5) return false;
                viewportHeight = floor;
                return true;
            }

            // Every row boundary, in order. The viewport has to end on one of these.
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
                // Taller than the cap: stop at the last boundary that still fits under it.
                wanted = MaxListHeight;
                foreach (double b in boundaries)
                {
                    if (b > MaxListHeight) break;
                    wanted = b;
                }
            }

            // Below the floor, grow to the next boundary at or past it rather than padding to a flat
            // number, which would leave a sliver of the following card showing.
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

        /// <summary>The plain "Refresh" row used by tabs with no other controls.</summary>
        protected void ComposeRefreshButton(Action onRefresh)
            => SingleComposer.AddSmallButton(Vintagestory.API.Config.Lang.Get("canevents:gui-refresh"),
                () => { onRefresh(); return true; }, ButtonAt(0, ControlsY), EnumButtonStyle.Normal);

        private void UpdateScrollbar()
        {
            var list = SingleComposer?.GetCellList<EventCardRow>("cardlist");
            var scrollbar = SingleComposer?.GetScrollbar("scrollbar");
            if (list == null || scrollbar == null || clipBounds == null) return;

            // Saved first: SetHeights fires TriggerChanged on the fresh scrollbar (position zero) and
            // that callback overwrites scrollOffset, jumping the list to the top on every recompose.
            float wanted = scrollOffset;

            scrollbar.Bounds.CalcWorldBounds();
            scrollbar.SetHeights((float)clipBounds.fixedHeight, (float)list.Bounds.fixedHeight);

            // A shorter list than before can leave the saved offset past the end, scrolling into blank space.
            float maxOffset = (float)Math.Max(0, list.Bounds.fixedHeight - clipBounds.fixedHeight);
            wanted = Math.Min(wanted, maxOffset);

            scrollOffset = wanted;
            scrollbar.CurrentYPosition = wanted;
            OnNewScrollbarValue(wanted);

            // Nothing to scroll -> no bar. Same test the base class uses to ignore input.
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
