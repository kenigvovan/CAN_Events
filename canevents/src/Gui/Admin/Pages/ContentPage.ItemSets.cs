using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers;
using canevents.src.Gui.Admin.Dialogs;
using canevents.src.Gui.Admin.GuiElements;
using canevents.src.Gui.Admin.Widgets;
using canevents.src.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace canevents.src.Gui.Admin.Pages
{
    public sealed class PendingItem
    {
        public string Code;
        public int Qty;
        // From the saved set; the server gives new items one on save.
        public string Id;
        // Loadouts: the match shop chain, in level order.
        public readonly List<UpgradeStepMsg> Upgrades = new List<UpgradeStepMsg>();
    }

    public sealed class ItemSetEditorBuffer
    {
        public string EventCode = "";
        public string SetName = "";
        public string SelectedEvent = "";
        public string SelectedSet = "";
        public readonly List<PendingItem> Pending = new List<PendingItem>();

        public int SelectedPending = -1;
    }

    // Loadouts and outfits share store shape and packets, so one editor for both.
    public sealed partial class ContentPage
    {
        private const double IconRowHeight = 32;
        private const double IconRowPitch = 38;
        private const double PlainRowPitch = 32;
        private const int MaxConfirmItems = 10;

        private readonly ItemSetEditorBuffer loadoutEditor = new ItemSetEditorBuffer();
        private readonly ItemSetEditorBuffer outfitEditor = new ItemSetEditorBuffer();

        private void BuildItemSets(PageBuildContext ctx, string kind)
        {
            // Outfits: one of each, no quantities, no hotbar import.
            bool isOutfit = kind == ItemSetKinds.Outfit;
            string p = isOutfit ? "content-of" : "content-lo";
            var st = isOutfit ? outfitEditor : loadoutEditor;
            var list = Net.ItemSetList(kind).Value;

            double top = ctx.Current.fixedY + 4;
            double leftWidth = Math.Floor((ctx.Width - Card.ColumnGap) * 0.48);
            var left = ctx.Column(0, leftWidth);
            var right = ctx.Column(leftWidth + Card.ColumnGap, ctx.Width - leftWidth - Card.ColumnGap);

            double y = BuildSetTable(ctx, left, top, st, list, isOutfit, p);
            BuildSetDetail(ctx, left, y, st, list, kind, isOutfit, p);
            BuildSetEditor(ctx, right, top, st, list, kind, isOutfit, p);
        }

        private double BuildSetTable(PageBuildContext ctx, ElementBounds column, double y, ItemSetEditorBuffer st,
                                     LoadoutListMsg list, bool isOutfit, string p)
        {
            var compo = ctx.Compo;
            double width = column.fixedWidth - Card.Padding * 2;
            string hint = Lang.Get(isOutfit ? "canevents:admin-content-outfits-hint" : "canevents:admin-content-loadouts-hint");
            double hintHeight = ButtonRow.TextHeight(hint, width);

            int setCount = list?.Events?.Sum(e => e.Sets?.Length ?? 0) ?? 0;
            string empty = Lang.Get(isOutfit ? "canevents:admin-content-outfits-empty" : "canevents:admin-content-loadouts-empty");
            string emptyHint = Lang.Get("canevents:admin-content-sets-empty-hint");

            double listHeight = Math.Min(Math.Max(setCount, 1), 4) * PlainRowPitch + 6;
            double body = hintHeight + 4 + (setCount > 0
                ? ColumnTable.HeaderHeight + 4 + listHeight + 8
                : Card.LineHeight + 4 + ButtonRow.TextHeight(emptyHint, width));
            double height = Card.HeightAround(body);

            var inner = Card.Frame(compo, column, y, height,
                Lang.Get(isOutfit ? "canevents:admin-content-outfits-title" : "canevents:admin-content-loadouts-title"));
            string kind = isOutfit ? ItemSetKinds.Outfit : ItemSetKinds.Loadout;
            Card.AddRefresh(ctx, column, y, p + "-refresh", () => Net.RequestLoadoutList(kind));

            double cy = inner.fixedY;
            compo.AddStaticText(hint, AdminFonts.Hint, ElementBounds.Fixed(inner.fixedX, cy, width, hintHeight), p + "-hint");
            cy += hintHeight + 4;

            if (setCount == 0)
            {
                AddLine(ctx, inner.fixedX, cy, width, empty, AdminColors.Muted, p + "-empty");
                cy += Card.LineHeight + 4;
                AddText(ctx, inner.fixedX, cy, width, emptyHint, AdminFonts.Hint, p + "-empty-hint");
                return y + height + Card.Gap;
            }

            var spec = new ColumnSpec(
                new ColumnDef(Lang.Get("canevents:admin-content-col-event"), 110),
                new ColumnDef(Lang.Get("canevents:admin-content-col-set"), 0),
                new ColumnDef(Lang.Get("canevents:admin-content-col-items"), 45, EnumTextOrientation.Right)) { RowHeight = 24 };

            var rows = new List<ColumnRow>(setCount);
            foreach (var ev in list.Events)
            {
                if (ev.Sets == null) continue;
                foreach (var set in ev.Sets)
                {
                    string eventCode = ev.EventCode;
                    string setName = set.Name;
                    var row = new ColumnRow(eventCode ?? "", setName ?? "", (set.ItemCodes?.Length ?? 0).ToString())
                        .WithColor(0, AdminColors.Muted)
                        .WithColor(2, AdminColors.Muted);
                    row.Selected = st.SelectedEvent == eventCode && st.SelectedSet == setName;
                    row.OnClick = () =>
                    {
                        st.SelectedEvent = eventCode;
                        st.SelectedSet = setName;
                        Rebuild();
                    };
                    rows.Add(row);
                }
            }

            ColumnTable.Add(ctx, ElementBounds.Fixed(inner.fixedX, cy, width, 0), spec, rows, listHeight,
                new ScrollableListOptions { Key = p + "-sets" });
            return y + height + Card.Gap;
        }

        private static LoadoutSetMsg FindSet(LoadoutListMsg list, string eventCode, string setName)
        {
            if (string.IsNullOrEmpty(eventCode) || list?.Events == null) return null;
            var ev = Array.Find(list.Events, e => e.EventCode == eventCode);
            return ev?.Sets != null ? Array.Find(ev.Sets, s => s.Name == setName) : null;
        }

        private static int QtyAt(LoadoutSetMsg set, int i)
            => set.ItemQuantities != null && i < set.ItemQuantities.Length ? set.ItemQuantities[i] : 1;

        private void BuildSetDetail(PageBuildContext ctx, ElementBounds column, double y, ItemSetEditorBuffer st,
                                    LoadoutListMsg list, string kind, bool isOutfit, string p)
        {
            var compo = ctx.Compo;
            double width = column.fixedWidth - Card.Padding * 2;
            double height = Math.Max(110, ctx.Bottom - y);

            var selSet = FindSet(list, st.SelectedEvent, st.SelectedSet);
            if (selSet == null)
            {
                var none = Card.Frame(compo, column, y, height, Lang.Get("canevents:admin-content-detail-title"));
                AddText(ctx, none.fixedX, none.fixedY, width, Lang.Get("canevents:admin-content-detail-none"),
                    AdminFonts.Hint, p + "-detail-none");
                return;
            }

            string eventCode = st.SelectedEvent;
            string setName = selSet.Name;
            var inner = Card.Frame(compo, column, y, height,
                Card.Fit(eventCode + " / " + setName, CairoFont.WhiteSmallishText(), width));

            double cy = inner.fixedY;
            var buttons = new ButtonRow(compo, ElementBounds.Fixed(inner.fixedX, cy, width, 0), width, 0, p + "-detail-btn");
            buttons.Add(Lang.Get("canevents:admin-content-load"), () =>
            {
                LoadIntoEditor(st, eventCode, selSet, p);
                Rebuild();
            }, Lang.Get("canevents:admin-content-load-tip"));

            if (!isOutfit)
            {
                buttons.Add(Lang.Get("canevents:admin-content-upgrades"), () => OpenKitUpgrades(eventCode, selSet),
                    Lang.Get("canevents:admin-content-upgrades-tip"));
            }

            buttons.Add(Lang.Get("canevents:admin-content-delete"), () =>
            {
                Gui.OpenSideDialog(new ConfirmPanel(
                    Lang.Get(isOutfit ? "canevents:admin-content-delete-outfit-title" : "canevents:admin-content-delete-set-title"),
                    DeleteQuestion(selSet, eventCode, isOutfit),
                    () =>
                    {
                        Act(Lang.Get("canevents:admin-content-log-delete-set", eventCode, setName),
                            () => Net.SendDeleteLoadoutSet(eventCode, setName, kind));
                        if (st.SelectedEvent == eventCode && st.SelectedSet == setName) st.SelectedSet = "";
                        Rebuild();
                    },
                    "canevents:admin-content-delete-confirm"));
            }, Lang.Get("canevents:admin-content-delete-tip"));

            var detailCaptions = new List<string> { Lang.Get("canevents:admin-content-load"), Lang.Get("canevents:admin-content-delete") };
            if (!isOutfit) detailCaptions.Add(Lang.Get("canevents:admin-content-upgrades"));
            cy += ButtonRow.HeightFor(width, 0, detailCaptions.ToArray()) + 6;

            int count = selSet.ItemCodes?.Length ?? 0;
            if (count == 0)
            {
                AddLine(ctx, inner.fixedX, cy, width, Lang.Get("canevents:admin-content-detail-empty"), AdminColors.Muted, p + "-detail-empty");
                return;
            }

            var spec = isOutfit
                ? new ColumnSpec(
                    new ColumnDef(null, 32),
                    new ColumnDef(Lang.Get("canevents:admin-content-col-item"), 0)) { RowHeight = IconRowHeight }
                : new ColumnSpec(
                    new ColumnDef(null, 32),
                    new ColumnDef(Lang.Get("canevents:admin-content-col-item"), 0),
                    new ColumnDef(Lang.Get("canevents:admin-content-col-qty"), 45, EnumTextOrientation.Right),
                    new ColumnDef(Lang.Get("canevents:admin-content-col-upgrades"), 45, EnumTextOrientation.Right)) { RowHeight = IconRowHeight };

            var rows = new List<ColumnRow>(count);
            for (int i = 0; i < count; i++)
            {
                string code = selSet.ItemCodes[i] ?? "";
                if (isOutfit)
                {
                    rows.Add(ItemRow(code, null));
                    continue;
                }
                int index = i;
                int levels = selSet.Upgrades?.Count(u => u.Item == index) ?? 0;
                rows.Add(ItemRow(new ColumnRow(null, code, QtyAt(selSet, i).ToString(), levels > 0 ? levels.ToString() : ""), code)
                    .WithColor(3, AdminColors.Section));
            }

            double innerBottom = inner.fixedY + inner.fixedHeight;
            double listHeight = Math.Max(40, innerBottom - cy - ColumnTable.HeaderHeight - 4 - 8);
            ColumnTable.Add(ctx, ElementBounds.Fixed(inner.fixedX, cy, width, 0), spec, rows, listHeight,
                new ScrollableListOptions { Key = p + "-detail-items" });
        }

        private void LoadIntoEditor(ItemSetEditorBuffer st, string eventCode, LoadoutSetMsg set, string p)
        {
            st.EventCode = eventCode;
            st.SetName = set.Name;
            st.Pending.Clear();
            st.SelectedPending = -1;
            for (int i = 0; i < (set.ItemCodes?.Length ?? 0); i++)
            {
                var item = new PendingItem
                {
                    Code = set.ItemCodes[i],
                    Qty = QtyAt(set, i),
                    Id = set.ItemIds != null && i < set.ItemIds.Length ? set.ItemIds[i] : null
                };
                int index = i;
                // Copies: the editor changes them, the server's list must stay as sent.
                item.Upgrades.AddRange((set.Upgrades ?? new UpgradeStepMsg[0]).Where(u => u.Item == index)
                    .Select(u => u.CopyFor(index)));
                st.Pending.Add(item);
            }
            State.Drafts.Remove(p + "-name");
            State.Drafts.Remove(p + "-event");
            State.Drafts.Remove(p + "-qty");
        }

        // From the shop tab, which only knows the kit's name.
        private void OpenKitUpgrades(string eventCode, string kitName)
        {
            var set = FindSet(Net.ItemSetList(ItemSetKinds.Loadout).Value, eventCode, kitName);
            if (set != null)
            {
                OpenKitUpgrades(eventCode, set);
                return;
            }
            Net.RequestLoadoutList(ItemSetKinds.Loadout);
            Gui.SetStatus(false, Lang.Get("canevents:admin-kitupg-loading"));
        }

        private void OpenKitUpgrades(string eventCode, LoadoutSetMsg set)
            => Gui.OpenSideDialog(new KitUpgradesPanel(eventCode, set, (ids, steps) => TakeSavedUpgrades(eventCode, set.Name, ids, steps)));

        // A kit loaded in the editor would save its old chains over the new ones otherwise.
        private void TakeSavedUpgrades(string eventCode, string setName, string[] ids, UpgradeStepMsg[] steps)
        {
            var st = loadoutEditor;
            if (st.EventCode != eventCode || st.SetName != setName) return;
            foreach (var item in st.Pending)
            {
                int index = item.Id == null ? -1 : Array.IndexOf(ids, item.Id);
                if (index < 0) continue;
                item.Upgrades.Clear();
                item.Upgrades.AddRange(steps.Where(s => s.Item == index).Select(s => s.CopyFor(index)));
            }
            Rebuild();
        }

        private static string DeleteQuestion(LoadoutSetMsg set, string eventCode, bool isOutfit)
        {
            var lines = new List<string>
            {
                Lang.Get("canevents:admin-content-delete-question", set.Name ?? "", eventCode ?? "")
            };
            int count = set.ItemCodes?.Length ?? 0;
            if (count == 0)
            {
                lines.Add(Lang.Get("canevents:admin-content-detail-empty"));
            }
            else
            {
                lines.Add(Lang.Get("canevents:admin-content-delete-items", count));
                for (int i = 0; i < count && i < MaxConfirmItems; i++)
                {
                    string code = set.ItemCodes[i] ?? "";
                    lines.Add(isOutfit ? "  " + code : "  " + code + " x" + QtyAt(set, i));
                }
                if (count > MaxConfirmItems) lines.Add(Lang.Get("canevents:admin-content-delete-more", count - MaxConfirmItems));
            }
            return string.Join("\n", lines);
        }

        // Column 0 = icon, column 1 = code.
        private ColumnRow ItemRow(string code, string qty)
            => ItemRow(qty == null ? new ColumnRow(null, code) : new ColumnRow(null, code, qty), code);

        private ColumnRow ItemRow(ColumnRow row, string code)
        {
            bool known = ItemIconRenderer.SlotFor(Capi, code) != null;
            row.IconCodes = new List<string> { code };
            row.IconColumn = 0;
            if (!known)
            {
                // Unresolved code (missing mod or typo).
                row.WithColor(1, AdminColors.Danger);
                row.WithCellTooltip(1, Lang.Get("canevents:admin-content-item-unknown"));
            }
            return row;
        }

        private void BuildSetEditor(PageBuildContext ctx, ElementBounds column, double y, ItemSetEditorBuffer st,
                                    LoadoutListMsg list, string kind, bool isOutfit, string p)
        {
            var compo = ctx.Compo;
            double width = column.fixedWidth - Card.Padding * 2;
            double height = Math.Max(200, ctx.Bottom - y);

            var eventCodes = list?.Events != null
                ? list.Events.Where(e => !string.IsNullOrEmpty(e.EventCode)).Select(e => e.EventCode).ToArray()
                : Array.Empty<string>();
            // Keep EventCode in sync with what the dropdown shows.
            if (eventCodes.Length > 0 && Array.IndexOf(eventCodes, st.EventCode) < 0) st.EventCode = eventCodes[0];
            if (st.SelectedPending >= st.Pending.Count) st.SelectedPending = -1;

            string title = st.Pending.Count > 0
                ? Lang.Get("canevents:admin-content-editor-title")
                : Lang.Get(isOutfit ? "canevents:admin-content-editor-create-outfit" : "canevents:admin-content-editor-create-set");
            var inner = Card.Frame(compo, column, y, height, title);
            double innerBottom = inner.fixedY + inner.fixedHeight;

            double cy = inner.fixedY;
            cy += AddText(ctx, inner.fixedX, cy, width,
                Lang.Get(isOutfit ? "canevents:admin-content-editor-hint-outfit" : "canevents:admin-content-editor-hint-set"),
                AdminFonts.Hint, p + "-ed-hint") + 4;

            string hotbar = Lang.Get("canevents:admin-content-add-hotbar");
            string armor = Lang.Get("canevents:admin-content-add-armor");
            string held = Lang.Get("canevents:admin-content-add-held");
            string clear = Lang.Get("canevents:admin-content-clear-list");
            string clearArmed = Lang.Get("canevents:admin-content-clear-list-armed");

            var buttons = new ButtonRow(compo, ElementBounds.Fixed(inner.fixedX, cy, width, 0), width, 0, p + "-ed-pull");
            var captions = new List<string>();
            if (!isOutfit)
            {
                buttons.Add(hotbar, () =>
                {
                    AddInventoryToPending(st, GlobalConstants.hotBarInvClassName);
                    Rebuild();
                }, Lang.Get("canevents:admin-content-add-hotbar-tip"));
                captions.Add(hotbar);
            }
            buttons.Add(armor, () =>
            {
                AddInventoryToPending(st, GlobalConstants.characterInvClassName);
                Rebuild();
            }, Lang.Get("canevents:admin-content-add-armor-tip"));
            captions.Add(armor);
            if (isOutfit)
            {
                buttons.Add(held, () =>
                {
                    AddHeldItemToPending(st);
                    Rebuild();
                }, Lang.Get("canevents:admin-content-add-held-tip"));
                captions.Add(held);
            }
            ArmedTextButton.Add(ctx, buttons.Next(ArmedTextButton.Width(clear, clearArmed)), p + "-ed-clear", clear, () =>
            {
                st.Pending.Clear();
                st.SelectedPending = -1;
            }, clearArmed);
            captions.Add(Longer(clear, clearArmed));
            cy += ButtonRow.HeightFor(width, 0, captions.ToArray()) + 6;

            // Measure what's below first, the list takes the rest.
            string save = Lang.Get("canevents:admin-content-save");
            string import = Lang.Get("canevents:admin-content-import");
            bool showQty = !isOutfit && st.Pending.Count > 0;
            double below = (showQty ? RowStep : 0) + RowStep * 2 + 4
                           + ButtonRow.HeightFor(width, 0, save, import) + Card.LineHeight + 4;

            if (st.Pending.Count > 0)
            {
                double listHeight = Math.Max(40, innerBottom - below - cy - ColumnTable.HeaderHeight - 4 - 8);
                BuildPendingTable(ctx, inner.fixedX, cy, width, listHeight, st, isOutfit, p);
                cy += ColumnTable.HeaderHeight + 4 + listHeight + 8;
            }
            else
            {
                AddLine(ctx, inner.fixedX, cy, width,
                    Lang.Get(isOutfit ? "canevents:admin-content-pending-empty-outfit" : "canevents:admin-content-pending-empty-set"),
                    AdminColors.Muted, p + "-pending-empty");
                cy = Math.Max(cy + Card.LineHeight + 4, innerBottom - below);
            }

            if (showQty)
            {
                BuildQtyRow(ctx, inner.fixedX, cy, width, st, p);
                cy += RowStep;
            }

            AddLabel(ctx, inner.fixedX, cy, Lang.Get("canevents:admin-content-event"), p + "-event-label");
            var evBounds = ElementBounds.Fixed(inner.fixedX + LabelWidth, cy, Math.Min(220, width - LabelWidth), InputHeight);
            if (eventCodes.Length > 0)
            {
                int idx = Math.Max(0, Array.IndexOf(eventCodes, st.EventCode));
                compo.AddDropDown(eventCodes, eventCodes, idx, (code, on) =>
                {
                    if (string.IsNullOrEmpty(code)) return;
                    st.EventCode = code;
                    Rebuild();
                }, evBounds, p + "-event-pick");
            }
            else
            {
                // No list yet, allow typing the code.
                NumberField.AddText(ctx, evBounds, p + "-event", () => st.EventCode, v =>
                {
                    st.EventCode = v ?? "";
                    UpdateSaveButtons(compo, st, p);
                });
            }
            cy += RowStep;

            AddLabel(ctx, inner.fixedX, cy, Lang.Get("canevents:admin-content-set-name"), p + "-name-label");
            NumberField.AddText(ctx, ElementBounds.Fixed(inner.fixedX + LabelWidth, cy, Math.Min(260, width - LabelWidth), InputHeight),
                p + "-name", () => st.SetName, v =>
                {
                    st.SetName = v ?? "";
                    // In place, a rebuild would wait for unfocus.
                    UpdateSaveButtons(compo, st, p);
                });
            cy += RowStep + 4;

            var actions = new ButtonRow(compo, ElementBounds.Fixed(inner.fixedX, cy, width, 0), width, 0, p + "-ed-save");
            compo.AddButton(save, new ActionConsumable(() =>
            {
                if (!CanSave(st)) return true;
                string saveEvent = st.EventCode.Trim();
                string saveName = st.SetName.Trim();
                var codes = st.Pending.Select(x => x.Code).ToArray();
                // Server ignores outfit quantities, sent to keep the packet shape.
                var quantities = st.Pending.Select(x => isOutfit ? 1 : x.Qty).ToArray();
                var ids = isOutfit ? null : st.Pending.Select(x => x.Id).ToArray();
                var upgrades = isOutfit ? null : st.Pending.SelectMany((x, i) => x.Upgrades.Select(u => u.CopyFor(i))).ToArray();
                Act(Lang.Get("canevents:admin-content-log-save-set", saveEvent, saveName, codes.Length),
                    () => Net.SendSaveLoadout(saveEvent, saveName, codes, quantities, kind, ids, upgrades));
                st.SetName = "";
                st.Pending.Clear();
                st.SelectedPending = -1;
                State.Drafts.Remove(p + "-name");
                Rebuild();
                return true;
            }), actions.Next(ButtonRow.ButtonWidth(save)), EnumButtonStyle.Normal, p + "-save");

            var importBounds = actions.Next(ButtonRow.ButtonWidth(import));
            compo.AddButton(import, new ActionConsumable(() =>
            {
                if (string.IsNullOrWhiteSpace(st.EventCode)) return true;
                string importEvent = st.EventCode.Trim();
                Act(Lang.Get("canevents:admin-content-log-import", importEvent), () => Net.SendImportInlineSets(importEvent, kind));
                return true;
            }), importBounds, EnumButtonStyle.Normal, p + "-import");
            Tooltip.Add(compo, Lang.Get(isOutfit ? "canevents:admin-content-import-tip-outfit" : "canevents:admin-content-import-tip-set"),
                importBounds, "tip-" + p + "-import");
            cy += ButtonRow.HeightFor(width, 0, save, import);

            compo.AddDynamicText("", AdminFonts.Hint, ElementBounds.Fixed(inner.fixedX, cy, width, Card.LineHeight + 2), p + "-save-hint");

            UpdateSaveButtons(compo, st, p, false);
            // Dynamic text can't be set before compose.
            ctx.AfterCompose(() => UpdateSaveButtons(compo, st, p));
        }

        private void BuildPendingTable(PageBuildContext ctx, double x, double y, double width, double listHeight,
                                       ItemSetEditorBuffer st, bool isOutfit, string p)
        {
            var spec = isOutfit
                ? new ColumnSpec(
                    new ColumnDef(null, 32),
                    new ColumnDef(Lang.Get("canevents:admin-content-col-item"), 0),
                    new ColumnDef(null, 30)) { RowHeight = IconRowHeight }
                : new ColumnSpec(
                    new ColumnDef(null, 32),
                    new ColumnDef(Lang.Get("canevents:admin-content-col-item"), 0),
                    new ColumnDef(Lang.Get("canevents:admin-content-col-qty"), 45, EnumTextOrientation.Right),
                    new ColumnDef(Lang.Get("canevents:admin-content-col-upgrades"), 45, EnumTextOrientation.Right),
                    new ColumnDef(null, 30)) { RowHeight = IconRowHeight };
            int removeCol = isOutfit ? 2 : 4;

            var rows = new List<ColumnRow>(st.Pending.Count);
            for (int i = 0; i < st.Pending.Count; i++)
            {
                int index = i;
                var item = st.Pending[i];
                var row = isOutfit
                    ? ItemRow(item.Code, null)
                    : ItemRow(new ColumnRow(null, item.Code, item.Qty.ToString(),
                        item.Upgrades.Count > 0 ? item.Upgrades.Count.ToString() : "", null), item.Code)
                        .WithColor(3, AdminColors.Section);

                if (!isOutfit)
                {
                    // Click selects the row for the qty field.
                    row.Selected = index == st.SelectedPending;
                    row.OnClick = () =>
                    {
                        st.SelectedPending = index;
                        State.Drafts.Remove(p + "-qty");
                        Rebuild();
                    };
                }

                row.WithButton(removeCol, "x", () =>
                {
                    if (index >= st.Pending.Count) return;
                    st.Pending.RemoveAt(index);
                    if (st.SelectedPending == index) st.SelectedPending = -1;
                    else if (st.SelectedPending > index) st.SelectedPending--;
                    State.Drafts.Remove(p + "-qty");
                    Rebuild();
                }, Lang.Get("canevents:admin-content-pending-remove-tip"), 24);
                rows.Add(row);
            }

            ColumnTable.Add(ctx, ElementBounds.Fixed(x, y, width, 0), spec, rows, listHeight,
                new ScrollableListOptions { Key = p + "-pending" });
        }

        private void BuildQtyRow(PageBuildContext ctx, double x, double y, double width, ItemSetEditorBuffer st, string p)
        {
            int sel = st.SelectedPending;
            if (sel < 0 || sel >= st.Pending.Count)
            {
                AddLine(ctx, x, y + 4, width, Lang.Get("canevents:admin-content-qty-pick"), AdminColors.Muted, p + "-qty-pick");
                return;
            }

            AddLabel(ctx, x, y, Lang.Get("canevents:admin-content-qty"), p + "-qty-label");
            NumberField.AddInt(ctx, ElementBounds.Fixed(x + LabelWidth, y, 70, InputHeight), p + "-qty",
                () => sel < st.Pending.Count ? st.Pending[sel].Qty : 1,
                v =>
                {
                    if (sel >= st.Pending.Count) return;
                    st.Pending[sel].Qty = Math.Max(1, v);
                    // Applies once the field loses focus.
                    Rebuild();
                },
                1, 9999);

            AddLine(ctx, x + LabelWidth + 80, y + 4, width - LabelWidth - 80, st.Pending[sel].Code, AdminColors.Label, p + "-qty-code");
        }

        private static bool CanSave(ItemSetEditorBuffer st)
            => st.Pending.Count > 0 && !string.IsNullOrWhiteSpace(st.EventCode) && !string.IsNullOrWhiteSpace(st.SetName);

        // Updated in place while typing.
        private static void UpdateSaveButtons(GuiComposer compo, ItemSetEditorBuffer st, string p, bool withText = true)
        {
            bool canSave = CanSave(st);
            var saveButton = compo.GetButton(p + "-save");
            if (saveButton != null) saveButton.Enabled = canSave;
            var importButton = compo.GetButton(p + "-import");
            if (importButton != null) importButton.Enabled = !string.IsNullOrWhiteSpace(st.EventCode);

            if (!withText) return;
            var hint = compo.GetDynamicText(p + "-save-hint");
            hint?.SetNewText(!canSave && st.Pending.Count > 0 ? Lang.Get("canevents:admin-content-save-missing") : "");
        }

        // Merges stacks of the same code.
        private void AddInventoryToPending(ItemSetEditorBuffer st, string inventoryClassName)
        {
            var inv = Capi?.World?.Player?.InventoryManager?.GetOwnInventory(inventoryClassName);
            if (inv == null) return;

            for (int i = 0; i < inv.Count; i++)
            {
                var stack = inv[i]?.Itemstack;
                if (stack?.Collectible?.Code == null) continue;

                string code = stack.Collectible.Code.ToString();
                var same = st.Pending.Find(x => x.Code == code);
                if (same != null) same.Qty += stack.StackSize;
                else st.Pending.Add(new PendingItem { Code = code, Qty = stack.StackSize });
            }
        }

        private void AddHeldItemToPending(ItemSetEditorBuffer st)
        {
            string code = Capi?.World?.Player?.InventoryManager?.ActiveHotbarSlot
                ?.Itemstack?.Collectible?.Code?.ToString();
            if (string.IsNullOrEmpty(code)) return;
            if (st.Pending.Any(x => x.Code == code)) return;
            st.Pending.Add(new PendingItem { Code = code, Qty = 1 });
        }

    }
}
