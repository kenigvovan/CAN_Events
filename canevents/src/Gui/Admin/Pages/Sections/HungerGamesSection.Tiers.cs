using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers.eventBehavior.Shared;
using canevents.src.EventPlacesManagers.Places;
using canevents.src.Gui.Admin.GuiElements;
using canevents.src.Gui.Admin.Widgets;
using canevents.src.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace canevents.src.Gui.Admin.Pages.Sections
{
    public sealed partial class HungerGamesSection
    {
        private double BuildTiers(PageBuildContext ctx, ElementBounds column, PlaceListEntryMsg place,
                                  HgSetupMsg s, string[] codes, double y)
        {
            var gui = ctx.Gui;
            var state = ctx.State;
            double width = column.fixedWidth - Card.Padding * 2;
            var tiers = buf.EditTiers;
            if (buf.TierSel >= tiers.Count) buf.TierSel = tiers.Count - 1;
            if (buf.TierSel < 0 && tiers.Count > 0) buf.TierSel = 0;
            HgTierEdit sel = buf.TierSel >= 0 && buf.TierSel < tiers.Count ? tiers[buf.TierSel] : null;

            double tierListHeight = Math.Min(Math.Max(tiers.Count, 1), 4) * ColumnTable.RowPitch + 10;
            double tierTableHeight = tiers.Count > 0 ? ColumnTable.HeaderHeight + 4 + tierListHeight + 10 : LineHeight + 4;

            string addTierCaption = Lang.Get("canevents:admin-hg-tier-add");

            int itemCount = sel?.Rows.Count ?? 0;
            double itemListHeight = Math.Min(Math.Max(itemCount, 1), MaxVisibleRows) * ColumnTable.RowPitch + 10;
            double itemTableHeight = itemCount > 0 ? ColumnTable.HeaderHeight + 4 + itemListHeight + 10 : LineHeight + 4;

            string itemAdd = Lang.Get("canevents:admin-hg-item-add");
            string itemHeld = Lang.Get("canevents:admin-hg-item-held");
            string saveCaption = Lang.Get("canevents:admin-hg-save");
            double itemButtonsHeight = ButtonRow.HeightFor(width, 0, itemAdd, itemHeld, saveCaption);

            double selBlock = sel != null
                ? LineHeight + 4 + itemTableHeight + InputRowHeight + itemButtonsHeight
                : 0;
            double contentHeight = tierTableHeight + InputRowHeight + selBlock + LineHeight + 4;

            var inner = Section.Begin(ctx, column, y, "hg-tiers", Lang.Get("canevents:admin-hg-tiers-title"),
                contentHeight, out double nextY);
            if (inner == null) return nextY;

            double cy = inner.fixedY;

            if (tiers.Count > 0)
            {
                var spec = new ColumnSpec(
                    new ColumnDef(Lang.Get("canevents:admin-hg-col-tier"), 0),
                    new ColumnDef(Lang.Get("canevents:admin-hg-col-items"), 60, EnumTextOrientation.Right),
                    new ColumnDef(null, 170));

                var rows = new List<ColumnRow>();
                for (int i = 0; i < tiers.Count; i++)
                {
                    var te = tiers[i];
                    int index = i;
                    var row = new ColumnRow(te.Name, te.Rows.Count.ToString(), null)
                    {
                        Selected = i == buf.TierSel,
                        OnClick = () =>
                        {
                            buf.TierSel = index;
                            gui.RequestRebuild();
                        }
                    };
                    row.WithColor(0, TierColor(i));
                    row.WithButton(2, Lang.Get("canevents:admin-hg-tier-open"), () =>
                    {
                        buf.TierSel = index;
                        gui.RequestRebuild();
                    }, Lang.Get("canevents:admin-hg-tier-open-hint"));
                    ArmedTextButton.AddToRow(ctx, row, 2, "hg-tier-rm-" + i,
                        Lang.Get("canevents:admin-hg-remove"), Lang.Get("canevents:admin-hg-remove-armed"),
                        Lang.Get("canevents:admin-hg-tier-remove-hint"),
                        () =>
                        {
                            tiers.Remove(te);
                            if (buf.TierSel >= tiers.Count) buf.TierSel = tiers.Count - 1;
                        });
                    rows.Add(row);
                }
                ColumnTable.Add(ctx, ElementBounds.Fixed(inner.fixedX, cy, width, 0), spec, rows, tierListHeight,
                    new ScrollableListOptions { Key = "hg-tier-list" });
            }
            else
            {
                ctx.Compo.AddStaticText(Lang.Get("canevents:admin-hg-tiers-empty"),
                    CairoFont.WhiteSmallText().WithColor(AdminColors.Warning),
                    ElementBounds.Fixed(inner.fixedX, cy, width, LineHeight), "hg-tiers-empty");
            }
            cy += tierTableHeight;

            double x = AddLabel(ctx, Lang.Get("canevents:admin-hg-tier-new"), inner.fixedX, cy, "hg-tier-new-label");
            var nameBounds = ElementBounds.Fixed(x, cy, 160, InputHeight);
            NumberField.AddText(ctx, nameBounds, "hg-tier-new",
                () => buf.NewTierName, v => buf.NewTierName = v, Lang.Get("canevents:admin-hg-tier-new-hint"));
            var addTierBounds = nameBounds.RightCopy(8).WithFixedSize(ButtonRow.ButtonWidth(addTierCaption), ButtonRow.ButtonHeight);
            ctx.Compo.AddButton(addTierCaption, new Vintagestory.API.Common.ActionConsumable(() =>
            {
                string nm = (buf.NewTierName ?? "").Trim();
                if (nm.Length == 0)
                {
                    gui.SetStatus(false, Lang.Get("canevents:admin-hg-tier-name-missing"));
                    return true;
                }
                int existing = tiers.FindIndex(t => string.Equals(t.Name, nm, StringComparison.OrdinalIgnoreCase));
                if (existing < 0)
                {
                    tiers.Add(new HgTierEdit { Name = nm });
                    existing = tiers.Count - 1;
                }
                buf.TierSel = existing;
                buf.NewTierName = "";
                state.Drafts.Remove("hg-tier-new");
                gui.RequestRebuild();
                return true;
            }), addTierBounds, EnumButtonStyle.Normal, "hg-tier-addbtn");
            cy += InputRowHeight;

            if (sel != null) BuildTierItems(ctx, inner.fixedX, cy, width, place, s, codes, sel, itemCount, itemListHeight, itemTableHeight);
            cy += selBlock;

            Card.AddDirtyLine(ctx, inner.fixedX, cy, width, "hg-tiers-dirty", buf.IsDirty, "canevents:admin-hg-");
            return nextY;
        }

        private void BuildTierItems(PageBuildContext ctx, double left, double cy, double width, PlaceListEntryMsg place,
                                    HgSetupMsg s, string[] codes, HgTierEdit te, int itemCount,
                                    double itemListHeight, double itemTableHeight)
        {
            var gui = ctx.Gui;
            var state = ctx.State;

            var headFont = CairoFont.WhiteSmallText().WithColor(TierColor(buf.TierSel));
            string head = Lang.Get("canevents:admin-hg-items-title", te.Name);
            ctx.Compo.AddStaticText(Card.Fit(head, headFont, width), headFont,
                ElementBounds.Fixed(left, cy, width, LineHeight), "hg-items-title");
            cy += LineHeight + 4;

            if (itemCount > 0)
            {
                var spec = new ColumnSpec(
                    new ColumnDef(null, 32),
                    new ColumnDef(Lang.Get("canevents:admin-hg-col-code"), 0),
                    new ColumnDef(Lang.Get("canevents:admin-hg-col-min"), 40, EnumTextOrientation.Right),
                    new ColumnDef(Lang.Get("canevents:admin-hg-col-max"), 40, EnumTextOrientation.Right),
                    new ColumnDef(Lang.Get("canevents:admin-hg-col-weight"), 55, EnumTextOrientation.Right),
                    new ColumnDef(null, 140));

                var rows = new List<ColumnRow>();
                foreach (var entry in te.Rows)
                {
                    var r = entry;
                    bool known = GuiItemUtil.Resolve(gui.Capi, r.Code) != null;
                    var row = new ColumnRow(null, r.Code, NumberParse.Format(r.Min), NumberParse.Format(r.Max),
                        NumberParse.Format(r.Weight), null)
                    {
                        IconCodes = new List<string> { r.Code },
                        IconColumn = 0,
                        Tooltip = known ? null : Lang.Get("canevents:admin-hg-item-unknown")
                    };
                    if (!known) row.WithColor(1, AdminColors.Danger);
                    row.WithButton(5, Lang.Get("canevents:admin-hg-edit"), () =>
                    {
                        // Add then overwrites this entry since the code matches.
                        buf.ItemCodeInput = r.Code;
                        buf.ItemMinInput = r.Min;
                        buf.ItemMaxInput = r.Max;
                        buf.ItemWeightInput = r.Weight;
                        state.Drafts.Remove("hg-item-code");
                        state.Drafts.Remove("hg-item-min");
                        state.Drafts.Remove("hg-item-max");
                        state.Drafts.Remove("hg-item-weight");
                        gui.RequestRebuild();
                    }, Lang.Get("canevents:admin-hg-item-edit-hint"));
                    row.WithButton(5, Lang.Get("canevents:admin-hg-delete"), () =>
                    {
                        te.Rows.Remove(r);
                        gui.RequestRebuild();
                    });
                    rows.Add(row);
                }
                ColumnTable.Add(ctx, ElementBounds.Fixed(left, cy, width, 0), spec, rows, itemListHeight,
                    new ScrollableListOptions { Key = "hg-item-list" });
            }
            else
            {
                ctx.Compo.AddStaticText(Lang.Get("canevents:admin-hg-items-empty"),
                    CairoFont.WhiteSmallText().WithColor(AdminColors.Warning),
                    ElementBounds.Fixed(left, cy, width, LineHeight), "hg-items-empty");
            }
            cy += itemTableHeight;

            NumberField.AddText(ctx, ElementBounds.Fixed(left, cy, 220, InputHeight), "hg-item-code",
                () => buf.ItemCodeInput, v => buf.ItemCodeInput = v, Lang.Get("canevents:admin-hg-item-code-hint"));
            double x = left + 220 + 10;
            x = AddLabel(ctx, Lang.Get("canevents:admin-hg-col-min"), x, cy, "hg-item-min-label");
            NumberField.AddInt(ctx, ElementBounds.Fixed(x, cy, 50, InputHeight), "hg-item-min",
                () => buf.ItemMinInput, v => buf.ItemMinInput = v, 0, 9999);
            x += 50 + 10;
            x = AddLabel(ctx, Lang.Get("canevents:admin-hg-col-max"), x, cy, "hg-item-max-label");
            NumberField.AddInt(ctx, ElementBounds.Fixed(x, cy, 50, InputHeight), "hg-item-max",
                () => buf.ItemMaxInput, v => buf.ItemMaxInput = v, 0, 9999);
            x += 50 + 10;
            x = AddLabel(ctx, Lang.Get("canevents:admin-hg-col-weight"), x, cy, "hg-item-weight-label");
            NumberField.Add(ctx, ElementBounds.Fixed(x, cy, 60, InputHeight), "hg-item-weight",
                () => buf.ItemWeightInput, v => buf.ItemWeightInput = v, 0, 100000);
            cy += InputRowHeight;

            var buttons = new ButtonRow(ctx.Compo, ElementBounds.Fixed(left, cy, width, 0), width, 0, "hg-item-btn");
            buttons.Add(Lang.Get("canevents:admin-hg-item-add"), () =>
            {
                string code = (buf.ItemCodeInput ?? "").Trim();
                if (code.Length == 0 || code.EndsWith(":"))
                {
                    gui.SetStatus(false, Lang.Get("canevents:admin-hg-item-code-missing"));
                    return;
                }
                int min = Math.Max(0, buf.ItemMinInput);
                int max = Math.Max(min, buf.ItemMaxInput);
                float weight = (float)Math.Max(0, buf.ItemWeightInput);
                var existing = te.Rows.Find(r => string.Equals(r.Code, code, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    existing.Min = min; existing.Max = max; existing.Weight = weight;
                }
                else
                {
                    te.Rows.Add(new HgLootRow { Code = code, Min = min, Max = max, Weight = weight });
                }
                gui.LogAction(true, Lang.Get("canevents:admin-hg-log-item", code, min, max, te.Name));
                gui.RequestRebuild();
            }, Lang.Get("canevents:admin-hg-item-add-hint"));
            buttons.Add(Lang.Get("canevents:admin-hg-item-held"), () =>
            {
                AddHeldItem(gui, te);
                gui.RequestRebuild();
            }, Lang.Get("canevents:admin-hg-item-held-hint"));
            buttons.Add(Lang.Get("canevents:admin-hg-save"), () => Save(gui, place, s, codes), Lang.Get("canevents:admin-hg-save-hint"));
        }

        // Existing code gets its range widened; a duplicate row would double its weight.
        private static void AddHeldItem(AdminPanelGui gui, HgTierEdit te)
        {
            if (!GuiItemUtil.TryReadHeldItem(gui.Capi, out var heldCode, out var heldQty))
            {
                gui.SetStatus(false, Lang.Get("canevents:admin-hg-hold-first"));
                return;
            }

            int qty = Math.Max(1, heldQty);
            var existing = te.Rows.Find(r => string.Equals(r.Code, heldCode, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                if (qty < existing.Min) existing.Min = qty;
                if (qty > existing.Max) existing.Max = qty;
                gui.LogAction(true, Lang.Get("canevents:admin-hg-log-held-widen", heldCode, te.Name, existing.Min, existing.Max));
            }
            else
            {
                te.Rows.Add(new HgLootRow { Code = heldCode, Min = qty, Max = qty, Weight = 1f });
                gui.LogAction(true, Lang.Get("canevents:admin-hg-log-item", heldCode, qty, qty, te.Name));
            }
        }
    }
}
