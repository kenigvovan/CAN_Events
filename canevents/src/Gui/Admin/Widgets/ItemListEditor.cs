using System;
using System.Collections.Generic;
using canevents.src.EventManagers.events;
using canevents.src.Gui.Admin.GuiElements;
using canevents.src.Gui.Admin.Pages;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace canevents.src.Gui.Admin.Widgets
{
    // Kept in the page's buffer so it survives rebuilds.
    public sealed class ItemListInput
    {
        public string Code = "";
        public int Quantity = 1;

        // SelectRow mode only; -1 = none.
        public int Selected = -1;
    }

    public enum ItemListEditMode
    {
        InputLine,

        // No input line, click a row and edit its quantity in place.
        SelectRow
    }

    public sealed class ItemListButton
    {
        public string Caption, Tooltip;
        public Action OnClick;

        // Needs a second click; null ArmedCaption uses the generic "click again".
        public bool SecondClick;
        public string ArmedCaption;
    }

    public sealed class ItemListEditorOptions
    {
        // Element key prefix, also the scroll memory key. Unique per page.
        public string Key = "items";

        // Already translated; null logs nothing.
        public string LogName;

        public ItemListEditMode EditMode = ItemListEditMode.InputLine;
        public bool ShowHotbar = true;
        public bool ShowHeld = true;
        public bool ShowClear = true;

        // "+ Held item" appends instead of updating an existing entry with the same code.
        public bool HeldAppends;

        public int MaxQuantity = 99999;

        // 0 = table fills the remaining area height.
        public int VisibleRows;

        // Greyed row shown for an empty list (what empty falls back to). Null uses EmptyText.
        public string FallbackCode;
        public int FallbackQuantity;
        public string FallbackTooltip;

        public string EmptyText;
        public double[] EmptyColor;

        public List<ItemListButton> Extra = new List<ItemListButton>();
    }

    // Mutates the given list in place; saving is up to the page.
    public static class ItemListEditor
    {
        private const double InputHeight = 26;
        private const double LineHeight = 20;
        private const double CodeWidth = 230;
        private const double QtyWidth = 64;
        private const double FallbackListHeight = 44;
        private const double RowPitch = 34;

        // Only meaningful with VisibleRows > 0.
        public static double Height(double width, List<RewardItem> items, ItemListEditorOptions opts)
            => CompactTableHeight(width, items.Count, opts) + InputHeight + 8 + ButtonsHeight(width, items.Count, opts);

        public static void Deselect(AdminGuiState state, ItemListInput input, string key)
        {
            input.Selected = -1;
            state.Drafts.Remove(key + "-sel-qty");
        }

        // area height is ignored when VisibleRows > 0.
        public static void Add(PageBuildContext ctx, ElementBounds area, List<RewardItem> items,
                               ItemListInput input, ItemListEditorOptions opts)
        {
            var compo = ctx.Compo;
            double x = area.fixedX;
            double width = area.fixedWidth;
            string key = opts.Key;
            if (input.Selected >= items.Count) input.Selected = -1;

            double inputRowHeight = InputHeight + 8;
            double tableSpace = opts.VisibleRows > 0
                ? CompactTableHeight(width, items.Count, opts)
                : area.fixedHeight - inputRowHeight - ButtonsHeight(width, items.Count, opts);
            double cy = area.fixedY;

            bool showFallback = items.Count == 0 && !string.IsNullOrWhiteSpace(opts.FallbackCode);
            if (items.Count > 0 || showFallback)
            {
                var rows = items.Count > 0 ? ItemRows(ctx, items, input, opts) : new List<ColumnRow> { FallbackRow(opts) };
                double listHeight = showFallback ? FallbackListHeight
                    : opts.VisibleRows > 0 ? Math.Min(items.Count, opts.VisibleRows) * RowPitch + 10
                    : Math.Max(40, tableSpace - ColumnTable.HeaderHeight - 4 - 10);
                ColumnTable.Add(ctx, ElementBounds.Fixed(x, cy, width, 0), Spec(opts), rows, listHeight,
                    new ScrollableListOptions { Key = key + "-list" });
            }
            if (items.Count == 0)
            {
                string text = showFallback ? opts.FallbackTooltip : EmptyText(opts);
                if (!string.IsNullOrEmpty(text))
                {
                    double textY = showFallback ? cy + ColumnTable.HeaderHeight + 4 + FallbackListHeight + 10 : cy;
                    double h = Math.Min(ButtonRow.TextHeight(text, width), Math.Max(LineHeight, area.fixedY + tableSpace - textY));
                    var font = CairoFont.WhiteSmallText().WithColor(showFallback ? AdminColors.Muted : opts.EmptyColor ?? AdminColors.Muted);
                    compo.AddStaticText(text, font, ElementBounds.Fixed(x, textY, width, h), key + "-empty");
                }
            }
            cy += tableSpace;

            if (opts.EditMode == ItemListEditMode.InputLine) AddInputLine(ctx, x, cy, items, input, opts);
            else AddSelectedQuantity(ctx, x, cy, items, input, opts);
            cy += inputRowHeight;

            AddButtons(ctx, x, cy, width, items, input, opts);
        }

        private static double CompactTableHeight(double width, int count, ItemListEditorOptions opts)
        {
            if (count > 0)
                return ColumnTable.HeaderHeight + 4 + Math.Min(count, opts.VisibleRows) * RowPitch + 10 + 10;
            if (!string.IsNullOrWhiteSpace(opts.FallbackCode))
                return ColumnTable.HeaderHeight + 4 + FallbackListHeight + 10
                       + (string.IsNullOrEmpty(opts.FallbackTooltip) ? 0 : ButtonRow.TextHeight(opts.FallbackTooltip, width) + 4);
            return ButtonRow.TextHeight(EmptyText(opts), width) + 4;
        }

        private static string EmptyText(ItemListEditorOptions opts)
            => opts.EmptyText ?? Lang.Get("canevents:admin-economy-items-empty");

        // Must list the same buttons as AddButtons.
        private static double ButtonsHeight(double width, int count, ItemListEditorOptions opts)
        {
            var captions = new List<string>();
            if (opts.ShowHotbar) captions.Add(Lang.Get("canevents:admin-economy-items-hotbar"));
            if (opts.ShowHeld) captions.Add(Lang.Get("canevents:admin-economy-items-held"));
            if (ShowsClear(count, opts))
                captions.Add(Widest(Lang.Get("canevents:admin-economy-items-clear"), Lang.Get("canevents:admin-economy-items-clear-armed")));
            foreach (var b in opts.Extra)
                captions.Add(b.SecondClick ? Widest(b.Caption, b.ArmedCaption ?? Lang.Get("canevents:admin-armed-confirm")) : b.Caption);
            return captions.Count == 0 ? 0 : ButtonRow.HeightFor(width, 0, captions.ToArray());
        }

        private static bool ShowsClear(int count, ItemListEditorOptions opts) => opts.ShowClear && count > 0;

        private static ColumnSpec Spec(ItemListEditorOptions opts)
        {
            var font = CairoFont.SmallButtonText(EnumButtonStyle.Small);
            double buttonsWidth = font.GetTextExtents(Lang.Get("canevents:admin-economy-items-remove")).Width + 16 + 4;
            if (opts.EditMode == ItemListEditMode.InputLine)
                buttonsWidth += font.GetTextExtents(Lang.Get("canevents:admin-economy-items-edit")).Width + 16 + 4;
            return new ColumnSpec(
                new ColumnDef(null, 32),
                new ColumnDef(Lang.Get("canevents:admin-economy-col-item"), 0),
                new ColumnDef(Lang.Get("canevents:admin-economy-col-qty"), 56, EnumTextOrientation.Right),
                new ColumnDef(null, Math.Max(10, buttonsWidth)));
        }

        private static List<ColumnRow> ItemRows(PageBuildContext ctx, List<RewardItem> items, ItemListInput input,
                                                ItemListEditorOptions opts)
        {
            var gui = ctx.Gui;
            var state = ctx.State;
            bool select = opts.EditMode == ItemListEditMode.SelectRow;
            var rows = new List<ColumnRow>(items.Count);
            for (int i = 0; i < items.Count; i++)
            {
                int index = i;
                var item = items[i];
                bool known = GuiItemUtil.Resolve(gui.Capi, item.Code) != null;
                var row = new ColumnRow(null, item.Code ?? "", NumberParse.Format(item.Quantity), null)
                {
                    IconCodes = new List<string> { item.Code },
                    IconColumn = 0
                };
                if (!known)
                {
                    row.WithColor(1, AdminColors.Danger);
                    row.WithCellTooltip(1, Lang.Get("canevents:admin-economy-items-unknown"));
                }
                if (select)
                {
                    row.Selected = index == input.Selected;
                    row.OnClick = () =>
                    {
                        Deselect(state, input, opts.Key);
                        input.Selected = index;
                        gui.RequestRebuild();
                    };
                }
                else
                {
                    row.WithButton(3, Lang.Get("canevents:admin-economy-items-edit"), () =>
                    {
                        // Add then overwrites this entry since the code matches.
                        input.Code = item.Code ?? "";
                        input.Quantity = Math.Max(1, item.Quantity);
                        state.Drafts.Remove(opts.Key + "-code");
                        state.Drafts.Remove(opts.Key + "-qty");
                        gui.RequestRebuild();
                    }, Lang.Get("canevents:admin-economy-items-edit-hint"));
                }
                row.WithButton(3, Lang.Get("canevents:admin-economy-items-remove"), () =>
                {
                    items.Remove(item);
                    Deselect(state, input, opts.Key);
                    Log(gui, opts, "canevents:admin-economy-log-remove", item.Code ?? "");
                    gui.RequestRebuild();
                });
                rows.Add(row);
            }
            return rows;
        }

        private static ColumnRow FallbackRow(ItemListEditorOptions opts)
        {
            var row = new ColumnRow(null, opts.FallbackCode, NumberParse.Format(opts.FallbackQuantity), null)
            {
                IconCodes = new List<string> { opts.FallbackCode },
                IconColumn = 0,
                Tooltip = opts.FallbackTooltip
            };
            row.WithColor(1, AdminColors.Muted);
            row.WithColor(2, AdminColors.Muted);
            return row;
        }

        private static void AddInputLine(PageBuildContext ctx, double x, double y, List<RewardItem> items,
                                         ItemListInput input, ItemListEditorOptions opts)
        {
            var gui = ctx.Gui;
            var compo = ctx.Compo;
            string key = opts.Key;

            var codeBounds = ElementBounds.Fixed(x, y, CodeWidth, InputHeight);
            NumberField.AddText(ctx, codeBounds, key + "-code", () => input.Code, v => input.Code = v,
                Lang.Get("canevents:admin-economy-items-code-hint"));
            var qtyBounds = codeBounds.RightCopy(6).WithFixedSize(QtyWidth, InputHeight);
            NumberField.AddInt(ctx, qtyBounds, key + "-qty", () => input.Quantity, v => input.Quantity = v, 1, opts.MaxQuantity,
                Lang.Get("canevents:admin-economy-items-qty-hint"));
            string addCaption = Lang.Get("canevents:admin-economy-items-add");
            var addBounds = qtyBounds.RightCopy(6).WithFixedSize(ButtonRow.ButtonWidth(addCaption), InputHeight);
            compo.AddButton(addCaption, new ActionConsumable(() =>
            {
                string code = (input.Code ?? "").Trim();
                if (code.Length == 0 || code.EndsWith(":"))
                {
                    gui.SetStatus(false, Lang.Get("canevents:admin-economy-items-code-missing"));
                    return true;
                }
                int qty = Math.Max(1, input.Quantity);
                AddOrSet(items, code, qty);
                Log(gui, opts, "canevents:admin-economy-log-item", code, qty);
                gui.RequestRebuild();
                return true;
            }), addBounds, EnumButtonStyle.Normal, key + "-add");
            Tooltip.Add(compo, Lang.Get("canevents:admin-economy-items-add-hint"), addBounds, "tip-" + key + "-add");
        }

        private static void AddSelectedQuantity(PageBuildContext ctx, double x, double y, List<RewardItem> items,
                                                ItemListInput input, ItemListEditorOptions opts)
        {
            if (input.Selected < 0) return;
            var item = items[input.Selected];
            string label = Lang.Get("canevents:admin-economy-col-qty");
            double labelWidth = CairoFont.WhiteSmallText().GetTextExtents(label).Width + 10;
            ctx.Compo.AddStaticText(label, AdminFonts.Body, ElementBounds.Fixed(x, y + 4, labelWidth, LineHeight), opts.Key + "-sel-label");
            NumberField.AddInt(ctx, ElementBounds.Fixed(x + labelWidth, y, QtyWidth, InputHeight), opts.Key + "-sel-qty",
                () => item.Quantity, v => item.Quantity = v, 1, opts.MaxQuantity,
                Lang.Get("canevents:admin-economy-items-selected-qty-hint"));
        }

        private static void AddButtons(PageBuildContext ctx, double x, double y, double width, List<RewardItem> items,
                                       ItemListInput input, ItemListEditorOptions opts)
        {
            var gui = ctx.Gui;
            string key = opts.Key;
            var buttons = new ButtonRow(ctx.Compo, ElementBounds.Fixed(x, y, width, 0), width, 0, key + "-btn");

            if (opts.ShowHotbar)
            {
                buttons.Add(Lang.Get("canevents:admin-economy-items-hotbar"), () =>
                {
                    var hotbar = GuiItemUtil.ReadInventory(gui.Capi, GlobalConstants.hotBarInvClassName);
                    if (hotbar.Count == 0)
                    {
                        gui.SetStatus(false, Lang.Get("canevents:admin-economy-items-hotbar-empty"));
                        return;
                    }
                    // Adds to an existing entry's quantity.
                    foreach (var (code, qty) in hotbar)
                    {
                        var existing = items.Find(i => i.Code == code);
                        if (existing != null) existing.Quantity += qty;
                        else items.Add(new RewardItem { Code = code, Quantity = qty });
                        Log(gui, opts, "canevents:admin-economy-log-item", code, qty);
                    }
                    gui.RequestRebuild();
                }, Lang.Get("canevents:admin-economy-items-hotbar-hint"));
            }

            if (opts.ShowHeld)
            {
                buttons.Add(Lang.Get("canevents:admin-economy-items-held"), () =>
                {
                    if (!GuiItemUtil.TryReadHeldItem(gui.Capi, out var code, out var qty))
                    {
                        gui.SetStatus(false, Lang.Get("canevents:admin-economy-hold-first"));
                        return;
                    }
                    qty = Math.Max(1, qty);
                    if (opts.HeldAppends) items.Add(new RewardItem { Code = code, Quantity = qty });
                    else AddOrSet(items, code, qty);
                    Log(gui, opts, "canevents:admin-economy-log-item", code, qty);
                    gui.RequestRebuild();
                }, Lang.Get("canevents:admin-economy-items-held-hint"));
            }

            if (ShowsClear(items.Count, opts))
            {
                string clear = Lang.Get("canevents:admin-economy-items-clear");
                string clearArmed = Lang.Get("canevents:admin-economy-items-clear-armed");
                ArmedTextButton.Add(ctx, buttons.Next(ArmedTextButton.Width(clear, clearArmed)), key + "-clear", clear, () =>
                {
                    items.Clear();
                    Deselect(ctx.State, input, key);
                    Log(gui, opts, "canevents:admin-economy-log-clear");
                }, clearArmed, Lang.Get("canevents:admin-economy-items-clear-hint"));
            }

            for (int i = 0; i < opts.Extra.Count; i++)
            {
                var b = opts.Extra[i];
                if (b.SecondClick)
                {
                    ArmedTextButton.Add(ctx, buttons.Next(ArmedTextButton.Width(b.Caption, b.ArmedCaption)),
                        key + "-extra-" + i, b.Caption, b.OnClick, b.ArmedCaption, b.Tooltip);
                }
                else
                {
                    buttons.Add(b.Caption, b.OnClick, b.Tooltip);
                }
            }
        }

        private static void AddOrSet(List<RewardItem> items, string code, int qty)
        {
            foreach (var entry in items)
            {
                if (string.Equals(entry.Code, code, StringComparison.OrdinalIgnoreCase))
                {
                    entry.Quantity = qty;
                    return;
                }
            }
            items.Add(new RewardItem { Code = code, Quantity = qty });
        }

        private static void Log(AdminPanelGui gui, ItemListEditorOptions opts, string langKey, params object[] args)
        {
            if (opts.LogName == null) return;
            var all = new object[args.Length + 1];
            all[0] = opts.LogName;
            Array.Copy(args, 0, all, 1, args.Length);
            gui.LogAction(true, Lang.Get(langKey, all));
        }

        private static string Widest(string a, string b)
            => b == null || ButtonRow.ButtonWidth(a) >= ButtonRow.ButtonWidth(b) ? a : b;
    }
}
