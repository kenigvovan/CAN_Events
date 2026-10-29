using System.Collections.Generic;
using canevents.src.Gui.Admin.Dialogs;
using canevents.src.Gui.Admin.GuiElements;
using canevents.src.Gui.Admin.Pages;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace canevents.src.Gui.Admin.Widgets
{
    // One shared edit line, not inline inputs: rebuilds drop focus. Same reason the filter waits for its button.
    public static class SettingsTable
    {
        private const double InputHeight = 26;
        private const double ScrollbarReserve = 27;

        // onApplied runs after any row write (flip, pick, Apply); the table rebuilds the page itself.
        public static void Add(PageBuildContext ctx, ElementBounds inner, string tableKey,
                               IReadOnlyList<SettingRow> rows, System.Action onApplied = null)
        {
            var compo = ctx.Compo;
            var state = ctx.State;
            var gui = ctx.Gui;

            string filterKey = tableKey + "-filter";
            string valueKey = tableKey + "-value";
            string listKey = tableKey + "-cells";
            string scrollbarKey = listKey + "-scrollbar";

            var filterBounds = inner.FlatCopy().WithFixedSize(130, InputHeight);
            NumberField.AddText(ctx, filterBounds, filterKey,
                () => state.SettingsFilter.TryGetValue(tableKey, out var f) ? f : "",
                v => state.SettingsFilter[tableKey] = v,
                Lang.Get("canevents:admin-settings-filter-hint"));

            string filterCaption = Lang.Get("canevents:admin-filter");
            var filterButton = filterBounds.RightCopy(6).WithFixedSize(ButtonRow.ButtonWidth(filterCaption), InputHeight);
            compo.AddButton(filterCaption, new ActionConsumable(() =>
            {
                state.Scroll[listKey] = 0;
                gui.RequestRebuild();
                return true;
            }), filterButton, EnumButtonStyle.Normal, tableKey + "-filterbtn");

            SettingRow editing = FindRow(rows, state.SettingsEditKey.TryGetValue(tableKey, out var ek) ? ek : null);

            string applyCaption = Lang.Get("canevents:admin-apply");
            double applyWidth = ButtonRow.ButtonWidth(applyCaption);
            double labelX = filterButton.fixedX + filterButton.fixedWidth + 14;
            double valueWidth = 120;
            double labelWidth = inner.fixedX + inner.fixedWidth - labelX - valueWidth - applyWidth - 12;

            var labelBounds = ElementBounds.Fixed(labelX, inner.fixedY + 5, System.Math.Max(40, labelWidth), InputHeight - 5);
            var labelFont = CairoFont.WhiteSmallText().WithColor(editing != null ? AdminColors.Section : AdminColors.Muted)
                .WithOrientation(EnumTextOrientation.Right);
            string labelText = editing != null ? editing.Label : Lang.Get("canevents:admin-settings-pick-row");
            compo.AddStaticText(Card.Fit(labelText, labelFont, labelBounds.fixedWidth), labelFont, labelBounds, tableKey + "-editlabel");

            var valueBounds = ElementBounds.Fixed(labelBounds.fixedX + labelBounds.fixedWidth + 6, inner.fixedY, valueWidth, InputHeight);
            NumberField.AddText(ctx, valueBounds, valueKey,
                () => state.SettingsEditValue.TryGetValue(tableKey, out var v) ? v : "",
                v => state.SettingsEditValue[tableKey] = v,
                editing?.RangeText());

            var applyBounds = valueBounds.RightCopy(6).WithFixedSize(applyWidth, InputHeight);
            compo.AddButton(applyCaption, new ActionConsumable(() =>
            {
                var row = FindRow(rows, state.SettingsEditKey.TryGetValue(tableKey, out var key) ? key : null);
                if (row == null) return true;

                string typed = state.SettingsEditValue.TryGetValue(tableKey, out var v) ? v : "";
                if (row.TryApply(typed, out string error))
                {
                    gui.LogAction(true, row.Label + " = " + row.CurrentValue());
                    onApplied?.Invoke();
                }
                else
                {
                    gui.SetStatus(false, Lang.Get(error));
                }
                gui.RequestRebuild();
                return true;
            }), applyBounds, EnumButtonStyle.Normal, tableKey + "-apply");

            var visible = VisibleRows(rows, state.SettingsFilter.TryGetValue(tableKey, out var filter) ? filter : "");
            string editingId = editing?.Id;

            double editRowsHeight = InputHeight + 8;

            // Clip must be added to the composer: the scissor push dereferences its parent.
            ElementBounds clipBounds = ElementBounds.Fixed(0, editRowsHeight,
                inner.fixedWidth - ScrollbarReserve, inner.fixedHeight - editRowsHeight);
            ElementBounds scrollbarBounds = clipBounds.RightCopy(7).WithFixedWidth(20);
            ElementBounds listBounds = clipBounds.ForkContainingChild(0, 0, 0, -3).WithFixedPadding(3);

            compo.BeginChildElements(inner)
                    .BeginClip(clipBounds)
                        // Keeps the list inside an outer ScrollPane clip.
                        .AddInteractiveElement(new NestedClipScissor(compo.Api, clipBounds))
                        .AddCellList(listBounds,
                            (SettingRow row, ElementBounds bounds) => new SettingsCell(compo.Api, row, bounds,
                                row.Id != null && row.Id == editingId)
                            {
                                On = true,
                                OnMouseDownOnCellLeft = index => OnRowClicked(ctx.Gui, state, tableKey, visible, index, onApplied)
                            },
                            visible, listKey)
                    .EndClip()
                    .AddInteractiveElement(new ScrollableList.ListWheelForwarder(compo, scrollbarKey,
                        ElementBounds.Fixed(clipBounds.fixedX, clipBounds.fixedY, clipBounds.fixedWidth, clipBounds.fixedHeight)),
                        listKey + "-wheel")
                    .AddVerticalScrollbar(value =>
                    {
                        // Kept across rebuilds.
                        state.Scroll[listKey] = value;

                        ElementBounds bounds = compo.GetCellList<SettingRow>(listKey).Bounds;
                        bounds.fixedY = 0 - value;
                        bounds.CalcWorldBounds();
                    }, scrollbarBounds, scrollbarKey)
                .EndChildElements();

            var cellList = compo.GetCellList<SettingRow>(listKey);
            cellList.unscaledCellSpacing = 3;
            cellList.UnscaledCellVerPadding = 1;
            cellList.BeforeCalcBounds();

            float restoreTo = state.ScrollOf(listKey);
            ctx.AfterCompose(() =>
            {
                var scrollbar = compo.GetScrollbar(scrollbarKey);
                float visibleHeight = (float)clipBounds.fixedHeight;
                float total = (float)compo.GetCellList<SettingRow>(listKey).Bounds.fixedHeight;
                scrollbar.SetHeights(visibleHeight, total);

                // Must follow SetHeights, otherwise it clamps against zero height.
                float wanted = System.Math.Clamp(restoreTo, 0f, System.Math.Max(0f, total - visibleHeight));
                if (wanted <= 0f) return;
                scrollbar.CurrentYPosition = wanted;
                scrollbar.TriggerChanged();
            });
        }

        private static SettingRow FindRow(IReadOnlyList<SettingRow> rows, string id)
        {
            if (id == null) return null;
            foreach (var row in rows)
                if (row.Id == id) return row;
            return null;
        }

        // Group headings are kept only if a row under them matches.
        private static List<SettingRow> VisibleRows(IReadOnlyList<SettingRow> rows, string filterText)
        {
            string filter = (filterText ?? "").Trim().ToLowerInvariant();
            var kept = new List<SettingRow>();
            if (filter.Length == 0)
            {
                kept.AddRange(rows);
                return kept;
            }

            SettingRow pendingGroup = null;
            foreach (var row in rows)
            {
                if (row.Kind == SettingKind.Group)
                {
                    pendingGroup = row;
                    continue;
                }

                bool matches = (row.Id ?? "").ToLowerInvariant().Contains(filter)
                            || (row.Label ?? "").ToLowerInvariant().Contains(filter);
                if (!matches) continue;

                if (pendingGroup != null)
                {
                    kept.Add(pendingGroup);
                    pendingGroup = null;
                }
                kept.Add(row);
            }
            return kept;
        }

        private static void OnRowClicked(AdminPanelGui gui, AdminGuiState state, string tableKey,
                                         List<SettingRow> rows, int index, System.Action onApplied)
        {
            if (index < 0 || index >= rows.Count) return;

            SettingRow row = rows[index];
            if (row.Kind == SettingKind.Group) return;

            if (row.Kind == SettingKind.Flag)
            {
                row.SetFlag(!row.GetFlag());
                gui.LogAction(true, row.Label + " = " + row.CurrentValue());
                onApplied?.Invoke();
                gui.RequestRebuild();
                return;
            }

            if (row.Kind == SettingKind.Choice)
            {
                gui.OpenSideDialog(new ChoicePanel(row, () =>
                {
                    gui.LogAction(true, row.Label + " = " + row.CurrentValue());
                    onApplied?.Invoke();
                }));
                return;
            }

            state.SettingsEditKey[tableKey] = row.Id;
            state.SettingsEditValue[tableKey] = row.CurrentValue();
            // Don't carry the previous row's draft over.
            state.Drafts.Remove(tableKey + "-value");
            gui.RequestRebuild();
        }
    }
}
