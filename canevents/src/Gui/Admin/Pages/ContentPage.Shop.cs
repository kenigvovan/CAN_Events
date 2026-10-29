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
    public sealed class ShopEditorBuffer
    {
        public string EventCode = "";

        // The server state the list was copied from. A newer one replaces the list unless it has unsaved edits.
        public ShopConfigMsg Source;
        public bool Dirty;

        public readonly List<ShopOfferConfigMsg> Offers = new List<ShopOfferConfigMsg>();
        public int StartPoints;
        public int PointsPerKill;
        public int PointsPerDeath;
        public int PointsPerRoundWin;
        public int BuyRadius;
        public bool BuyAnywhere;
        public bool ShowPreview;
    }

    // Match shop editor. Left: the point rules, sent whole with Save together with the one-time items.
    // Right: the kits with their upgrade chains (edited in KitUpgradesPanel, saved with the kit) and the
    // one-time items (edited in ShopOfferPanel).
    public sealed partial class ContentPage
    {
        private const string ShopP = "content-shop";
        private const double ShopSwitch = 20;
        private const double ShopLabelWidth = 150;

        private readonly ShopEditorBuffer shopEditor = new ShopEditorBuffer();

        private static ShopOfferConfigMsg Copy(ShopOfferConfigMsg o) => new ShopOfferConfigMsg
        {
            Id = o.Id,
            Code = o.Code,
            Quantity = o.Quantity,
            Cost = o.Cost,
            MaxBuys = o.MaxBuys,
            DropOnDeath = o.DropOnDeath,
            Kits = o.Kits?.ToArray()
        };

        private void SyncShop(ShopConfigMsg cfg)
        {
            var st = shopEditor;
            if (cfg == null || ReferenceEquals(cfg, st.Source)) return;
            bool sameEvent = cfg.EventCode == st.EventCode;
            st.Source = cfg;
            if (st.Dirty && sameEvent) return;

            st.EventCode = cfg.EventCode ?? "";
            st.Offers.Clear();
            if (cfg.Offers != null) st.Offers.AddRange(cfg.Offers.Select(Copy));
            st.StartPoints = cfg.StartPoints;
            st.PointsPerKill = cfg.PointsPerKill;
            st.PointsPerDeath = cfg.PointsPerDeath;
            st.PointsPerRoundWin = cfg.PointsPerRoundWin;
            st.BuyRadius = cfg.BuyRadius;
            st.BuyAnywhere = cfg.BuyAnywhere;
            st.ShowPreview = cfg.ShowPreview;
            st.Dirty = false;
            foreach (var key in new[] { "-start", "-kill", "-death", "-round", "-radius" }) State.Drafts.Remove(ShopP + key);
        }

        private void MarkShopDirty()
        {
            shopEditor.Dirty = true;
            Rebuild();
        }

        private void BuildShop(PageBuildContext ctx)
        {
            var cfg = Net.ShopConfig.Value;
            SyncShop(cfg);

            double top = ctx.Current.fixedY + 4;
            double leftWidth = Math.Floor((ctx.Width - Card.ColumnGap) * 0.42);
            var left = ctx.Column(0, leftWidth);
            var right = ctx.Column(leftWidth + Card.ColumnGap, ctx.Width - leftWidth - Card.ColumnGap);

            BuildShopSettings(ctx, left, top, cfg);
            if (cfg == null || string.IsNullOrEmpty(shopEditor.EventCode) || (cfg.EventCodes?.Length ?? 0) == 0) return;

            double y = BuildKitChains(ctx, right, top, cfg);
            BuildOfferList(ctx, right, y, cfg);
        }

        private void BuildShopSettings(PageBuildContext ctx, ElementBounds column, double y, ShopConfigMsg cfg)
        {
            var compo = ctx.Compo;
            var st = shopEditor;
            double width = column.fixedWidth - Card.Padding * 2;
            var inner = Card.Frame(compo, column, y, Math.Max(200, ctx.Bottom - y), Lang.Get("canevents:admin-shop-title"));
            Card.AddRefresh(ctx, column, y, ShopP + "-refresh", () =>
            {
                st.Dirty = false;
                st.Source = null;
                Net.RequestShopConfig(st.EventCode);
                Net.RequestLoadoutList(ItemSetKinds.Loadout);
            });
            double innerBottom = inner.fixedY + inner.fixedHeight;
            double x = inner.fixedX;
            double cy = inner.fixedY;

            cy += AddText(ctx, x, cy, width, Lang.Get("canevents:admin-shop-hint"), AdminFonts.Hint, ShopP + "-hint") + 6;

            if (cfg == null)
            {
                AddLine(ctx, x, cy, width, Lang.Get("canevents:admin-shop-loading"), AdminColors.Muted, ShopP + "-loading");
                return;
            }
            var codes = cfg.EventCodes ?? new string[0];
            if (codes.Length == 0)
            {
                AddText(ctx, x, cy, width, Lang.Get("canevents:admin-shop-no-events"), AdminFonts.Hint, ShopP + "-none");
                return;
            }

            AddLabel(ctx, x, cy, Lang.Get("canevents:admin-content-event"), ShopP + "-event-label");
            compo.AddDropDown(codes, codes, Math.Max(0, Array.IndexOf(codes, st.EventCode)), (code, selected) =>
            {
                if (string.IsNullOrEmpty(code) || code == st.EventCode) return;
                st.EventCode = code;
                st.Dirty = false;
                st.Source = null;
                st.Offers.Clear();
                Net.RequestShopConfig(code);
                Rebuild();
            }, ElementBounds.Fixed(x + LabelWidth, cy, Math.Min(220, width - LabelWidth), InputHeight), ShopP + "-event");
            cy += RowStep;

            string eventCode = st.EventCode;
            bool on = cfg.Enabled && cfg.EventCode == eventCode;
            string toggle = Lang.Get(on ? "canevents:admin-shop-disable" : "canevents:admin-shop-enable");
            double toggleWidth = ButtonRow.ButtonWidth(toggle);
            AddLine(ctx, x, cy + 4, width - toggleWidth - 8, Lang.Get(on ? "canevents:admin-shop-on" : "canevents:admin-shop-off"),
                on ? AdminColors.Success : AdminColors.Muted, ShopP + "-status");
            compo.AddSmallButton(toggle, new ActionConsumable(() =>
            {
                Act(Lang.Get(on ? "canevents:admin-shop-log-disable" : "canevents:admin-shop-log-enable", eventCode),
                    () => Net.SendShopEnable(eventCode, !on));
                return true;
            }), ElementBounds.Fixed(x + width - toggleWidth, cy, toggleWidth, 24), EnumButtonStyle.Normal, ShopP + "-toggle");
            cy += RowStep + 6;

            ShopIntField(ctx, x, cy, width, "-start", "canevents:admin-shop-start", () => st.StartPoints, v => st.StartPoints = v, 0);
            cy += RowStep;
            ShopIntField(ctx, x, cy, width, "-kill", "canevents:admin-shop-kill", () => st.PointsPerKill, v => st.PointsPerKill = v, -1000);
            cy += RowStep;
            ShopIntField(ctx, x, cy, width, "-death", "canevents:admin-shop-death", () => st.PointsPerDeath, v => st.PointsPerDeath = v, -1000);
            cy += RowStep;
            ShopIntField(ctx, x, cy, width, "-round", "canevents:admin-shop-round", () => st.PointsPerRoundWin, v => st.PointsPerRoundWin = v, -1000);
            cy += RowStep;
            ShopIntField(ctx, x, cy, width, "-radius", "canevents:admin-shop-radius", () => st.BuyRadius, v => st.BuyRadius = v, 1);
            cy += RowStep + 4;

            cy += ShopSwitchRow(ctx, x, cy, width, ShopP + "-anywhere", Lang.Get("canevents:admin-shop-anywhere"),
                st.BuyAnywhere, v => { st.BuyAnywhere = v; MarkShopDirty(); }) + 8;
            ShopSwitchRow(ctx, x, cy, width, ShopP + "-preview", Lang.Get("canevents:admin-shop-preview"),
                st.ShowPreview, v => { st.ShowPreview = v; MarkShopDirty(); });

            string save = Lang.Get("canevents:admin-content-save");
            string revert = Lang.Get("canevents:admin-shop-revert");
            cy = innerBottom - ButtonRow.HeightFor(width, 0, save, revert) - Card.LineHeight - 8;

            var actions = new ButtonRow(compo, ElementBounds.Fixed(x, cy, width, 0), width, 0, ShopP + "-save-row");
            actions.Add(save, () =>
            {
                var msg = new ShopConfigMsg
                {
                    EventCode = eventCode,
                    Offers = st.Offers.Select(Copy).ToArray(),
                    StartPoints = st.StartPoints,
                    PointsPerKill = st.PointsPerKill,
                    PointsPerDeath = st.PointsPerDeath,
                    PointsPerRoundWin = st.PointsPerRoundWin,
                    BuyRadius = st.BuyRadius,
                    BuyAnywhere = st.BuyAnywhere,
                    ShowPreview = st.ShowPreview
                };
                Act(Lang.Get("canevents:admin-shop-log-save", eventCode, msg.Offers.Length), () => Net.SendSaveShopConfig(msg));
                st.Dirty = false;
                Rebuild();
            }, Lang.Get("canevents:admin-shop-save-tip"));
            actions.Add(revert, () =>
            {
                st.Dirty = false;
                st.Source = null;
                Rebuild();
            });
            cy += ButtonRow.HeightFor(width, 0, save, revert) + 4;
            Card.AddDirtyLine(ctx, x, cy, width, ShopP + "-dirty", st.Dirty, "canevents:admin-shop-");
        }

        private void ShopIntField(PageBuildContext ctx, double x, double y, double width, string key, string labelKey,
            Func<int> get, Action<int> set, int min)
        {
            ctx.Compo.AddStaticText(Card.Fit(Lang.Get(labelKey), AdminFonts.Body, ShopLabelWidth - 6), AdminFonts.Body,
                ElementBounds.Fixed(x, y + 4, ShopLabelWidth - 6, 20), ShopP + key + "-label");
            NumberField.AddInt(ctx, ElementBounds.Fixed(x + ShopLabelWidth, y, Math.Min(80, width - ShopLabelWidth), InputHeight),
                ShopP + key, get, v =>
                {
                    if (v == get()) return;
                    set(v);
                    MarkShopDirty();
                }, min, 100000);
        }

        // Returns the height taken; the caption wraps instead of being cut.
        private static double ShopSwitchRow(PageBuildContext ctx, double x, double y, double width, string key, string text,
            bool value, Action<bool> onToggle)
        {
            var compo = ctx.Compo;
            compo.AddSwitch(v => onToggle(v), ElementBounds.Fixed(x, y, ShopSwitch, ShopSwitch), key, ShopSwitch, 3);
            compo.GetSwitch(key).On = value;
            double textWidth = width - ShopSwitch - 8;
            double height = ButtonRow.TextHeight(text, textWidth);
            compo.AddStaticText(text, AdminFonts.Body, ElementBounds.Fixed(x + ShopSwitch + 8, y + 1, textWidth, height), key + "-label");
            return Math.Max(ShopSwitch, height);
        }

        // The event's kits with how many of their items have a chain; "Set up" opens the chain window.
        private double BuildKitChains(PageBuildContext ctx, ElementBounds column, double y, ShopConfigMsg cfg)
        {
            var compo = ctx.Compo;
            string eventCode = shopEditor.EventCode;
            double width = column.fixedWidth - Card.Padding * 2;
            var kits = cfg.KitNames ?? new string[0];
            string import = Lang.Get("canevents:admin-shop-kits-import");
            string inline = Lang.Get("canevents:admin-shop-kits-inline");
            string hint = Lang.Get("canevents:admin-shop-chains-hint");

            double listHeight = Math.Min(Math.Max(kits.Length, 1), 4) * PlainRowPitch + 6;
            double body = ButtonRow.TextHeight(hint, width) + 4 + (kits.Length == 0
                ? Card.LineHeight + 4
                : !cfg.KitsManaged
                    ? ButtonRow.TextHeight(inline, width) + 4 + ButtonRow.HeightFor(width, 0, import)
                    : ColumnTable.HeaderHeight + 4 + listHeight + 4);
            double height = Card.HeightAround(body);

            var inner = Card.Frame(compo, column, y, height, Lang.Get("canevents:admin-shop-chains-title"));
            double x = inner.fixedX;
            double cy = inner.fixedY;
            cy += AddText(ctx, x, cy, width, hint, AdminFonts.Hint, ShopP + "-chains-hint") + 4;

            if (kits.Length == 0)
            {
                AddLine(ctx, x, cy, width, Lang.Get("canevents:admin-shop-no-kits"), AdminColors.Muted, ShopP + "-chains-none");
                return y + height + Card.Gap;
            }

            if (!cfg.KitsManaged)
            {
                cy += AddText(ctx, x, cy, width, inline, AdminFonts.Hint, ShopP + "-inline") + 4;
                var row = new ButtonRow(compo, ElementBounds.Fixed(x, cy, width, 0), width, 0, ShopP + "-import");
                row.Add(import, () =>
                {
                    Act(Lang.Get("canevents:admin-content-log-import", eventCode), () =>
                    {
                        Net.SendImportInlineSets(eventCode, ItemSetKinds.Loadout);
                        Net.RequestShopConfig(eventCode);
                    });
                });
                return y + height + Card.Gap;
            }

            var spec = new ColumnSpec(
                new ColumnDef(Lang.Get("canevents:admin-shop-col-kit"), 0),
                new ColumnDef(Lang.Get("canevents:admin-shop-col-chains"), 110, EnumTextOrientation.Right),
                new ColumnDef(null, 110)) { RowHeight = 24 };

            var rows = new List<ColumnRow>(kits.Length);
            for (int i = 0; i < kits.Length; i++)
            {
                string kit = kits[i];
                int chains = cfg.KitChains != null && i < cfg.KitChains.Length ? cfg.KitChains[i] : 0;
                var row = new ColumnRow(kit, chains > 0 ? chains.ToString() : "—", null)
                    .WithColor(1, chains > 0 ? AdminColors.Success : AdminColors.Muted);
                row.WithButton(2, Lang.Get("canevents:admin-shop-chains-edit"), () => OpenKitUpgrades(eventCode, kit),
                    Lang.Get("canevents:admin-shop-chains-edit-tip"));
                rows.Add(row);
            }

            ColumnTable.Add(ctx, ElementBounds.Fixed(x, cy, width, 0), spec, rows, listHeight,
                new ScrollableListOptions { Key = ShopP + "-kits" });
            return y + height + Card.Gap;
        }

        private void BuildOfferList(PageBuildContext ctx, ElementBounds column, double y, ShopConfigMsg cfg)
        {
            var compo = ctx.Compo;
            var st = shopEditor;
            double width = column.fixedWidth - Card.Padding * 2;
            var inner = Card.Frame(compo, column, y, Math.Max(120, ctx.Bottom - y), Lang.Get("canevents:admin-shop-items-title"));
            double innerBottom = inner.fixedY + inner.fixedHeight;
            double x = inner.fixedX;
            double cy = inner.fixedY;

            string add = Lang.Get("canevents:admin-shop-add");
            var buttons = new ButtonRow(compo, ElementBounds.Fixed(x, cy, width, 0), width, 0, ShopP + "-add-row");
            buttons.Add(add, () => OpenOffer(-1, cfg), Lang.Get("canevents:admin-shop-add-tip"));
            cy += ButtonRow.HeightFor(width, 0, add) + 6;

            if (st.Offers.Count == 0)
            {
                AddText(ctx, x, cy, width, Lang.Get("canevents:admin-shop-empty"), AdminFonts.Hint, ShopP + "-empty");
                return;
            }

            var spec = new ColumnSpec(
                new ColumnDef(null, 32),
                new ColumnDef(Lang.Get("canevents:admin-content-col-item"), 0),
                new ColumnDef(Lang.Get("canevents:admin-shop-col-kits"), 100),
                new ColumnDef(Lang.Get("canevents:admin-shop-col-cost"), 45, EnumTextOrientation.Right),
                new ColumnDef(null, 30)) { RowHeight = IconRowHeight };

            var rows = new List<ColumnRow>(st.Offers.Count);
            for (int i = 0; i < st.Offers.Count; i++)
            {
                int index = i;
                var o = st.Offers[i];
                string kits = o.Kits == null || o.Kits.Length == 0
                    ? Lang.Get("canevents:admin-shop-kits-all") : string.Join(", ", o.Kits);
                string item = ItemName(o.Code) + (o.Quantity > 1 ? " ×" + o.Quantity : "");

                var row = new ColumnRow(null, item, kits, o.Cost.ToString(), null)
                    .WithColor(2, AdminColors.Muted)
                    .WithCellTooltip(1, o.Code);
                row.IconCodes = new List<string> { o.Code };
                row.IconColumn = 0;
                row.OnClick = () => OpenOffer(index, cfg);
                row.WithButton(4, "x", () => RemoveOffer(index), Lang.Get("canevents:admin-content-pending-remove-tip"), 24);
                rows.Add(row);
            }

            double listHeight = Math.Max(40, innerBottom - cy - ColumnTable.HeaderHeight - 4 - 8);
            ColumnTable.Add(ctx, ElementBounds.Fixed(x, cy, width, 0), spec, rows, listHeight,
                new ScrollableListOptions { Key = ShopP + "-offers" });
        }

        // index -1: a new item.
        private void OpenOffer(int index, ShopConfigMsg cfg)
        {
            var st = shopEditor;
            var offer = index >= 0 && index < st.Offers.Count
                ? Copy(st.Offers[index])
                : new ShopOfferConfigMsg { Quantity = 1, Cost = 1, MaxBuys = 1 };
            Gui.OpenSideDialog(new ShopOfferPanel(offer, cfg.KitNames ?? new string[0], index >= 0, edited =>
            {
                if (index >= 0 && index < st.Offers.Count)
                {
                    edited.Id = st.Offers[index].Id;
                    st.Offers[index] = edited;
                }
                else
                {
                    edited.Id = UniqueOfferId(edited.Code);
                    st.Offers.Add(edited);
                }
                MarkShopDirty();
            }));
        }

        private void RemoveOffer(int index)
        {
            var st = shopEditor;
            if (index >= st.Offers.Count) return;
            st.Offers.RemoveAt(index);
            MarkShopDirty();
        }

        private string ItemName(string code)
            => string.IsNullOrEmpty(code) ? "?" : GuiItemUtil.Resolve(Capi, code)?.GetName() ?? code;

        // "game:ore-bomb" -> "ore-bomb", "-2" and up when taken.
        private string UniqueOfferId(string code)
        {
            int colon = code.IndexOf(':');
            string root = colon >= 0 ? code.Substring(colon + 1) : code;
            string id = root;
            for (int n = 2; shopEditor.Offers.Any(o => o.Id == id); n++) id = root + "-" + n;
            return id;
        }
    }
}
