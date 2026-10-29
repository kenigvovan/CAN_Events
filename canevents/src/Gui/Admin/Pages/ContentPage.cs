using System;
using System.Collections.Generic;
using canevents.src.EventManagers;
using canevents.src.EventPlacesManagers.Places;
using canevents.src.Gui.Admin.GuiElements;
using canevents.src.Gui.Admin.Widgets;
using canevents.src.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace canevents.src.Gui.Admin.Pages
{
    // Loadouts/outfits share one editor in ContentPage.ItemSets.cs; the match shop is in ContentPage.Shop.cs.
    public sealed partial class ContentPage : AdminPageBase
    {
        private const int SubLoadouts = 0;
        private const int SubOutfits = 1;
        private const int SubShop = 2;
        private const int SubTemplates = 3;
        private const int SubSchematics = 4;

        private const double InputHeight = 26;
        private const double RowStep = 30;
        private const double LabelWidth = 120;

        private static readonly string[] SubTabs =
        {
            "canevents:admin-content-sub-loadouts",
            "canevents:admin-content-sub-outfits",
            "canevents:admin-content-sub-shop",
            "canevents:admin-content-sub-templates",
            "canevents:admin-content-sub-schematics"
        };

        protected override PanelTab Tab => PanelTab.Content;
        protected override string[] SubTabLangKeys => SubTabs;

        public override void Watch(PageWatchList watch)
        {
            watch.Add(Net.ItemSetList(ItemSetKinds.Loadout))
                 .Add(Net.ItemSetList(ItemSetKinds.Outfit))
                 .Add(Net.ShopConfig)
                 .Add(Net.TemplateList)
                 .Add(Net.TplSession)
                 .Add(Net.SchematicList);
        }

        public override void OnShown()
        {
            // Loadouts are already requested by the tab's standard request.
            if (SubTab != SubLoadouts) RequestFor(SubTab);
        }

        protected override void OnSubTabChanged(int subTab) => RequestFor(subTab);

        private void RequestFor(int subTab)
        {
            switch (subTab)
            {
                case SubLoadouts: Net.RequestLoadoutList(ItemSetKinds.Loadout); break;
                case SubOutfits: Net.RequestLoadoutList(ItemSetKinds.Outfit); break;
                case SubShop:
                    Net.RequestShopConfig(shopEditor.EventCode);
                    // The kit upgrade window opens from the saved sets.
                    Net.RequestLoadoutList(ItemSetKinds.Loadout);
                    break;
                case SubTemplates: Net.RequestTemplateList(); break;
                case SubSchematics: Net.RequestSchematicList(); break;
            }
        }

        protected override void BuildSubTab(PageBuildContext ctx, int subTab)
        {
            switch (subTab)
            {
                case SubLoadouts: BuildItemSets(ctx, ItemSetKinds.Loadout); break;
                case SubOutfits: BuildItemSets(ctx, ItemSetKinds.Outfit); break;
                case SubShop: BuildShop(ctx); break;
                case SubTemplates: BuildTemplates(ctx); break;
                case SubSchematics: BuildSchematics(ctx); break;
            }
        }

        private const int MaxOffsetRows = 3;
        private const double SmallRowPitch = 30;

        private void BuildTemplates(PageBuildContext ctx)
        {
            var column = ctx.Column(0, ctx.Width);
            double top = ctx.Current.fixedY + 4;

            var session = Net.TplSession.Value;
            bool editing = session != null && session.InSession;

            double sessionHeight = editing ? TplEditorHeight(session, column.fixedWidth - Card.Padding * 2) : 0;
            double listBottom = editing ? ctx.Bottom - sessionHeight - Card.Gap : ctx.Bottom;

            BuildTemplateList(ctx, column, top, listBottom);
            if (editing) BuildTplEditor(ctx, column, listBottom + Card.Gap, sessionHeight, session);
        }

        private void BuildTemplateList(PageBuildContext ctx, ElementBounds column, double y, double bottom)
        {
            var compo = ctx.Compo;
            double width = column.fixedWidth - Card.Padding * 2;
            double height = Math.Max(120, bottom - y);

            var inner = Card.Frame(compo, column, y, height, Lang.Get("canevents:admin-content-tpl-title"));
            Card.AddRefresh(ctx, column, y, "content-tpl-refresh", () => Net.RequestTemplateList());

            double cy = inner.fixedY;
            cy += AddText(ctx, inner.fixedX, cy, width, Lang.Get("canevents:admin-content-tpl-hint"), AdminFonts.Hint, "content-tpl-hint") + 4;

            var templates = Net.TemplateList.Value?.Templates;
            if (templates == null || templates.Length == 0)
            {
                cy += AddText(ctx, inner.fixedX, cy, width, Lang.Get("canevents:admin-content-tpl-empty"),
                    CairoFont.WhiteSmallText().WithColor(AdminColors.Muted), "content-tpl-empty") + 4;
                AddText(ctx, inner.fixedX, cy, width, Lang.Get("canevents:admin-content-tpl-empty-hint"),
                    AdminFonts.Hint, "content-tpl-empty-hint");
                return;
            }

            var spec = new ColumnSpec(
                new ColumnDef(Lang.Get("canevents:admin-content-tpl-col-code"), 140),
                new ColumnDef(Lang.Get("canevents:admin-content-tpl-col-types"), 160),
                new ColumnDef(Lang.Get("canevents:admin-content-tpl-col-mode"), 90),
                new ColumnDef(Lang.Get("canevents:admin-content-tpl-col-desc"), 0),
                new ColumnDef(null, 170));

            var rows = new List<ColumnRow>(templates.Length);
            foreach (var t in templates) rows.Add(TemplateRow(t));

            double innerBottom = inner.fixedY + inner.fixedHeight;
            double listHeight = Math.Max(40, innerBottom - cy - ColumnTable.HeaderHeight - 4 - 8);
            ColumnTable.Add(ctx, ElementBounds.Fixed(inner.fixedX, cy, width, 0), spec, rows, listHeight,
                new ScrollableListOptions { Key = "content-tpl-list" });
        }

        private ColumnRow TemplateRow(TemplateListEntryMsg t)
        {
            string code = t.Code ?? "";
            var row = new ColumnRow(code, t.PlaceTypes ?? "",
                    Lang.Get(t.OnDemand ? "canevents:admin-content-tpl-ondemand" : "canevents:admin-content-tpl-persistent"),
                    t.Description ?? "", null)
                .WithColor(1, AdminColors.Muted)
                .WithColor(2, t.OnDemand ? AdminColors.Warning : AdminColors.Muted)
                .WithColor(3, AdminColors.Label);

            row.WithButton(4, Lang.Get("canevents:admin-content-tpl-place"), () =>
                Act(Lang.Get("canevents:admin-content-log-place-template", code), () => Net.SendPlaceTemplate(code)),
                Lang.Get("canevents:admin-content-tpl-place-tip"));
            row.WithButton(4, Lang.Get("canevents:admin-content-tpl-edit"), () => SendTpl(TplAction.Start, code),
                Lang.Get("canevents:admin-content-tpl-edit-tip"));
            return row;
        }

        private void SendTpl(TplAction action, string code = null)
        {
            string what = Lang.Get("canevents:admin-content-log-tpl-" + action.ToString().ToLowerInvariant());
            string template = code ?? Net.TplSession.Value?.TemplateCode ?? "?";
            Act(Lang.Get("canevents:admin-content-log-tpl", what, template), () => Net.SendTplAction(action, code));
        }

        private static double TplEditorHeight(TplSessionStateMsg s, double width)
        {
            int count = s.OffsetsX?.Length ?? 0;
            double body = Card.LineHeight + 4                 // origin line
                          + ButtonRow.HeightFor(width, 0, TplEditorCaptions()) + 6
                          + Card.LineHeight + 4;              // "Spawn offsets (n):"
            if (count > 0) body += ColumnTable.HeaderHeight + 4 + OffsetListHeight(count) + 8;
            return Card.HeightAround(body);
        }

        private static double OffsetListHeight(int count) => Math.Min(Math.Max(count, 1), MaxOffsetRows) * SmallRowPitch + 6;

        // Armed buttons measured at their wider caption.
        private static string[] TplEditorCaptions() => new[]
        {
            Lang.Get("canevents:admin-content-tpl-add-spawn"),
            Longer(Lang.Get("canevents:admin-content-tpl-clear"), Lang.Get("canevents:admin-content-tpl-clear-armed")),
            Lang.Get("canevents:admin-content-tpl-save"),
            Longer(Lang.Get("canevents:admin-content-tpl-cancel"), Lang.Get("canevents:admin-content-tpl-cancel-armed")),
            Lang.Get("canevents:admin-content-tpl-finish")
        };

        private void BuildTplEditor(PageBuildContext ctx, ElementBounds column, double y, double height, TplSessionStateMsg s)
        {
            var compo = ctx.Compo;
            double width = column.fixedWidth - Card.Padding * 2;
            var inner = Card.Frame(compo, column, y, height,
                Lang.Get("canevents:admin-content-tpl-editing", s.TemplateCode ?? "?"));

            double cy = inner.fixedY;
            AddLine(ctx, inner.fixedX, cy, width,
                Lang.Get("canevents:admin-content-tpl-origin", s.OriginX, s.OriginY, s.OriginZ, s.PlaceId),
                AdminColors.Muted, "content-tpl-origin");
            cy += Card.LineHeight + 4;

            var buttons = new ButtonRow(compo, ElementBounds.Fixed(inner.fixedX, cy, width, 0), width, 0, "content-tpl-btn");
            buttons.Add(Lang.Get("canevents:admin-content-tpl-add-spawn"), () => SendTpl(TplAction.AddSpawn));

            string clear = Lang.Get("canevents:admin-content-tpl-clear");
            string clearArmed = Lang.Get("canevents:admin-content-tpl-clear-armed");
            ArmedTextButton.Add(ctx, buttons.Next(ArmedTextButton.Width(clear, clearArmed)), "content-tpl-clear", clear,
                () => SendTpl(TplAction.Clear), clearArmed);

            buttons.Add(Lang.Get("canevents:admin-content-tpl-save"), () => SendTpl(TplAction.Save));

            string cancel = Lang.Get("canevents:admin-content-tpl-cancel");
            string cancelArmed = Lang.Get("canevents:admin-content-tpl-cancel-armed");
            ArmedTextButton.Add(ctx, buttons.Next(ArmedTextButton.Width(cancel, cancelArmed)), "content-tpl-cancel", cancel,
                () => SendTpl(TplAction.Cancel), cancelArmed);

            buttons.Add(Lang.Get("canevents:admin-content-tpl-finish"), () => SendTpl(TplAction.Finish));

            cy += ButtonRow.HeightFor(width, 0, TplEditorCaptions()) + 6;

            int count = s.OffsetsX?.Length ?? 0;
            AddLine(ctx, inner.fixedX, cy, width, Lang.Get("canevents:admin-content-tpl-offsets", count),
                AdminColors.Muted, "content-tpl-offsets");
            cy += Card.LineHeight + 4;
            if (count == 0) return;

            var spec = new ColumnSpec(
                new ColumnDef("#", 40),
                new ColumnDef(Lang.Get("canevents:admin-content-tpl-col-offset"), 0)) { RowHeight = 22 };

            var rows = new List<ColumnRow>(count);
            for (int i = 0; i < count; i++)
            {
                int ox = s.OffsetsX[i];
                int oy = s.OffsetsY != null && i < s.OffsetsY.Length ? s.OffsetsY[i] : 0;
                int oz = s.OffsetsZ != null && i < s.OffsetsZ.Length ? s.OffsetsZ[i] : 0;
                rows.Add(new ColumnRow(i.ToString(), ox + ", " + oy + ", " + oz).WithColor(0, AdminColors.Muted));
            }

            ColumnTable.Add(ctx, ElementBounds.Fixed(inner.fixedX, cy, width, 0), spec, rows, OffsetListHeight(count),
                new ScrollableListOptions { Key = "content-tpl-offsets-list" });
        }

        private static readonly string[] TypeNames = PlaceTags.Known;

        private const string SchCodeKey = "content-sch-code";
        private const string SchCreateKey = "content-sch-create";
        private const double SwitchSize = 22;

        private string schematicNewCode = "";
        private readonly bool[] schematicSelectedTypes = new bool[TypeNames.Length];
        // null = form closed
        private string schematicFormTarget;
        private bool schematicOnDemand;

        private void BuildSchematics(PageBuildContext ctx)
        {
            double top = ctx.Current.fixedY + 4;
            bool formOpen = schematicFormTarget != null;

            // Form is tall (all type switches), so it goes beside the list.
            double listWidth = formOpen ? Math.Floor((ctx.Width - Card.ColumnGap) * 0.55) : ctx.Width;
            BuildSchematicList(ctx, ctx.Column(0, listWidth), top);
            if (formOpen)
                BuildSchematicForm(ctx, ctx.Column(listWidth + Card.ColumnGap, ctx.Width - listWidth - Card.ColumnGap), top);
        }

        private void BuildSchematicList(PageBuildContext ctx, ElementBounds column, double y)
        {
            var compo = ctx.Compo;
            double width = column.fixedWidth - Card.Padding * 2;
            double height = Math.Max(120, ctx.Bottom - y);

            var inner = Card.Frame(compo, column, y, height, Lang.Get("canevents:admin-content-sch-title"));
            Card.AddRefresh(ctx, column, y, "content-sch-refresh", () => Net.RequestSchematicList());

            double cy = inner.fixedY;
            cy += AddText(ctx, inner.fixedX, cy, width, Lang.Get("canevents:admin-content-sch-hint"), AdminFonts.Hint, "content-sch-hint") + 4;

            var list = Net.SchematicList.Value?.Schematics;
            if (list == null || list.Length == 0)
            {
                cy += AddText(ctx, inner.fixedX, cy, width, Lang.Get("canevents:admin-content-sch-empty"),
                    CairoFont.WhiteSmallText().WithColor(AdminColors.Muted), "content-sch-empty") + 4;
                AddText(ctx, inner.fixedX, cy, width, Lang.Get("canevents:admin-content-sch-empty-hint"),
                    AdminFonts.Hint, "content-sch-empty-hint");
                return;
            }

            var spec = new ColumnSpec(
                new ColumnDef(Lang.Get("canevents:admin-content-sch-col-name"), 0),
                new ColumnDef(Lang.Get("canevents:admin-content-sch-col-size"), 90),
                new ColumnDef(Lang.Get("canevents:admin-content-sch-col-template"), 75),
                new ColumnDef(null, 140));

            var rows = new List<ColumnRow>(list.Length);
            foreach (var s in list)
            {
                string name = s.Name ?? "";
                var row = new ColumnRow(name, s.SizeX + "x" + s.SizeY + "x" + s.SizeZ,
                        Lang.Get(s.HasTemplate ? "canevents:admin-content-sch-yes" : "canevents:admin-content-sch-no"), null)
                    .WithColor(1, AdminColors.Muted)
                    .WithColor(2, s.HasTemplate ? AdminColors.Success : AdminColors.Muted);
                row.Selected = schematicFormTarget != null && schematicFormTarget == s.Name;
                row.WithButton(3, Lang.Get("canevents:admin-content-sch-create-template"), () =>
                {
                    schematicFormTarget = s.Name;
                    schematicNewCode = s.Name ?? "";
                    for (int i = 0; i < schematicSelectedTypes.Length; i++) schematicSelectedTypes[i] = false;
                    State.Drafts.Remove(SchCodeKey);
                    Rebuild();
                });
                rows.Add(row);
            }

            double innerBottom = inner.fixedY + inner.fixedHeight;
            double listHeight = Math.Max(40, innerBottom - cy - ColumnTable.HeaderHeight - 4 - 8);
            ColumnTable.Add(ctx, ElementBounds.Fixed(inner.fixedX, cy, width, 0), spec, rows, listHeight,
                new ScrollableListOptions { Key = "content-sch-list" });
        }

        private void BuildSchematicForm(PageBuildContext ctx, ElementBounds column, double y)
        {
            var compo = ctx.Compo;
            double width = column.fixedWidth - Card.Padding * 2;
            double height = Math.Max(120, ctx.Bottom - y);

            var inner = Card.Frame(compo, column, y, height,
                Card.Fit(Lang.Get("canevents:admin-content-sch-form-title", schematicFormTarget),
                    CairoFont.WhiteSmallishText(), width));

            double cy = inner.fixedY;
            AddLabel(ctx, inner.fixedX, cy, Lang.Get("canevents:admin-content-sch-code"), "content-sch-code-label");
            var codeBounds = ElementBounds.Fixed(inner.fixedX + LabelWidth, cy, width - LabelWidth, InputHeight);
            NumberField.AddText(ctx, codeBounds, SchCodeKey, () => schematicNewCode, v =>
            {
                schematicNewCode = v ?? "";
                // In place, a rebuild would wait for unfocus.
                UpdateCreateButton(compo);
            });
            cy += RowStep + 2;

            AddLine(ctx, inner.fixedX, cy, width, Lang.Get("canevents:admin-content-sch-types"), AdminColors.Label, "content-sch-types");
            cy += Card.LineHeight + 4;

            double colWidth = width / 2;
            for (int i = 0; i < TypeNames.Length; i++)
            {
                int index = i;
                double sx = inner.fixedX + (i % 2) * colWidth;
                double sy = cy + (i / 2) * (SwitchSize + 6);
                string key = "content-sch-type-" + i;
                compo.AddSwitch(on =>
                {
                    schematicSelectedTypes[index] = on;
                    UpdateCreateButton(compo);
                }, ElementBounds.Fixed(sx, sy, SwitchSize, SwitchSize), key, SwitchSize, 3);
                compo.GetSwitch(key).On = schematicSelectedTypes[i];
                compo.AddStaticText(TypeNames[i], AdminFonts.Body,
                    ElementBounds.Fixed(sx + SwitchSize + 6, sy + 2, colWidth - SwitchSize - 10, 20), key + "-label");
            }
            cy += ((TypeNames.Length + 1) / 2) * (SwitchSize + 6) + 4;

            compo.AddSwitch(on => schematicOnDemand = on,
                ElementBounds.Fixed(inner.fixedX, cy, SwitchSize, SwitchSize), "content-sch-ondemand", SwitchSize, 3);
            compo.GetSwitch("content-sch-ondemand").On = schematicOnDemand;
            string onDemand = Lang.Get("canevents:admin-content-sch-ondemand");
            double odWidth = width - SwitchSize - 6;
            double odHeight = ButtonRow.TextHeight(onDemand, odWidth);
            compo.AddStaticText(onDemand, AdminFonts.Body,
                ElementBounds.Fixed(inner.fixedX + SwitchSize + 6, cy + 2, odWidth, odHeight), "content-sch-ondemand-label");
            cy += Math.Max(SwitchSize, odHeight) + 10;

            var buttons = new ButtonRow(compo, ElementBounds.Fixed(inner.fixedX, cy, width, 0), width, 0, "content-sch-form");
            string create = Lang.Get("canevents:admin-content-sch-create");
            compo.AddButton(create, new ActionConsumable(() =>
            {
                string code = schematicNewCode?.Trim();
                var selected = SelectedTypes();
                if (string.IsNullOrEmpty(code) || selected.Length == 0) return true;
                string schematic = schematicFormTarget;
                bool createOnDemand = schematicOnDemand;
                Act(Lang.Get("canevents:admin-content-log-sch-create", code, schematic),
                    () => Net.SendCreateTemplateFromSchematic(schematic, code, selected, createOnDemand));
                schematicFormTarget = null;
                schematicNewCode = "";
                schematicOnDemand = false;
                State.Drafts.Remove(SchCodeKey);
                Rebuild();
                return true;
            }), buttons.Next(ButtonRow.ButtonWidth(create)), EnumButtonStyle.Normal, SchCreateKey);
            Tooltip.Add(compo, Lang.Get("canevents:admin-content-sch-create-tip"), compo.GetButton(SchCreateKey).Bounds, "tip-" + SchCreateKey);

            buttons.Add(Lang.Get("canevents:admin-cancel"), () =>
            {
                schematicFormTarget = null;
                Rebuild();
            });

            UpdateCreateButton(compo);
        }

        private string[] SelectedTypes()
        {
            var selected = new List<string>();
            for (int i = 0; i < TypeNames.Length; i++)
            {
                if (schematicSelectedTypes[i]) selected.Add(TypeNames[i]);
            }
            return selected.ToArray();
        }

        private void UpdateCreateButton(GuiComposer compo)
        {
            var button = compo.GetButton(SchCreateKey);
            if (button == null) return;
            button.Enabled = !string.IsNullOrWhiteSpace(schematicNewCode) && Array.IndexOf(schematicSelectedTypes, true) >= 0;
        }

        // Returns the wrapped height.
        private static double AddText(PageBuildContext ctx, double x, double y, double width, string text, CairoFont font, string key)
        {
            double h = ButtonRow.TextHeight(text, width);
            ctx.Compo.AddStaticText(text, font, ElementBounds.Fixed(x, y, width, h), key);
            return h;
        }

        private static void AddLine(PageBuildContext ctx, double x, double y, double width, string text, double[] color, string key)
        {
            var font = CairoFont.WhiteSmallText().WithColor(color);
            Card.AddFitLine(ctx.Compo, text, font, ElementBounds.Fixed(x, y, width, Card.LineHeight + 2), key, key + "-tip");
        }

        private static void AddLabel(PageBuildContext ctx, double x, double y, string text, string key)
        {
            ctx.Compo.AddStaticText(Card.Fit(text, AdminFonts.Body, LabelWidth - 6), AdminFonts.Body,
                ElementBounds.Fixed(x, y + 4, LabelWidth - 6, 20), key);
        }

        private static string Longer(string a, string b) => ArmedTextButton.WiderCaption(a, b);
    }
}
