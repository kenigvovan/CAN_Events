using System;
using System.Collections.Generic;
using canevents.src.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace canevents.src.Gui.Native
{
    // Match shop window. Opened by the server's answer to the hotkey, so it never shows outside a match
    // with a shop; every purchase comes back as a fresh state and recomposes it.
    public class MatchShopGui : GuiDialog
    {
        public const string HotkeyCode = "canevents-shop";

        private const double Width = 620;
        private const double RowHeight = 34;
        private const double NameWidth = 350;

        private readonly ClientNetworkHandler networkHandler;

        public MatchShopGui(ICoreClientAPI capi, ClientNetworkHandler networkHandler) : base(capi)
        {
            this.networkHandler = networkHandler;
        }

        public override string ToggleKeyCombinationCode => null;

        public static string HotkeyName(ICoreClientAPI capi)
            => capi.Input.GetHotKeyByCode(HotkeyCode)?.CurrentMapping?.ToString() ?? "B";

        public ShopPreviewGui Preview;

        // The hotkey: asks the server, which answers with the state and Open set. While queued it shows
        // the queued event's shop instead.
        public bool OnHotkey()
        {
            if (IsOpened()) return TryClose();
            if (Preview?.IsOpened() == true) return Preview.TryClose();
            if (networkHandler.ActiveEventId == 0)
            {
                var queued = networkHandler.QueuedEntry();
                if (queued == null || !queued.HasShop || Preview == null) return false;
                Preview.Show(queued.GameModeCode, null);
                return true;
            }
            networkHandler.RequestShop();
            return true;
        }

        // Wired to ClientNetworkHandler.OnShopOpenRequested.
        public void OnOpenRequested()
        {
            Compose();
            TryOpen();
        }

        // Wired to ClientNetworkHandler.OnShopChanged.
        public void OnStateChanged()
        {
            if (!IsOpened()) return;
            if (networkHandler.Shop == null) TryClose();
            else Compose();
        }

        private void Compose()
        {
            var shop = networkHandler.Shop;
            if (shop == null) return;

            var composer = DialogShell.Begin(capi, "canevents-shop",
                Lang.Get("canevents:gui-shop-title", (shop.EventCode ?? "").ToUpperInvariant()), () => TryClose());
            double y = DialogShell.BodyTop;

            composer.AddStaticText(Lang.Get("canevents:gui-shop-points", shop.Points),
                CairoFont.WhiteSmallishText().WithWeight(Cairo.FontWeight.Bold).WithColor(Palette.Title),
                ElementBounds.Fixed(0, y, Width, 26));
            y += 30;

            var upgrades = shop.Upgrades ?? new ShopUpgradeMsg[0];
            var offers = shop.Offers ?? new ShopOfferMsg[0];
            if (upgrades.Length == 0 && offers.Length == 0)
            {
                composer.AddStaticText(Lang.Get("canevents:gui-shop-nothing"), CairoFont.WhiteSmallText().WithColor(Palette.Detail),
                    ElementBounds.Fixed(0, y, Width, 20));
                y += 26;
            }

            if (upgrades.Length > 0)
            {
                y = SectionHeader(composer, y, Lang.Get("canevents:gui-shop-upgrades", shop.Kit ?? ""),
                    "canevents:gui-shop-upgrades-hint");
                foreach (var upgrade in upgrades)
                {
                    ComposeUpgrade(composer, y, upgrade, shop.Points);
                    y += RowHeight;
                }
            }

            if (offers.Length > 0)
            {
                y = SectionHeader(composer, y, Lang.Get("canevents:gui-shop-items"), "canevents:gui-shop-items-hint");
                if (!shop.BuyAnywhere)
                {
                    composer.AddStaticText(Lang.Get("canevents:gui-shop-where", shop.BuyRadius),
                        CairoFont.WhiteDetailText().WithColor(Palette.Detail), ElementBounds.Fixed(0, y, Width, 20));
                    y += 22;
                }
                foreach (var offer in offers)
                {
                    ComposeOffer(composer, y, offer, shop.Points);
                    y += RowHeight;
                }
            }

            composer.AddSmallButton(Lang.Get("canevents:gui-shop-close"), () => TryClose(),
                ElementBounds.Fixed(EnumDialogArea.LeftFixed, 0, y + 6, 0, 0).WithFixedPadding(10, 4),
                EnumButtonStyle.Normal);
            if (shop.Preview)
            {
                string eventCode = shop.EventCode;
                composer.AddSmallButton(Lang.Get("canevents:gui-shop-all"), () => { Preview?.Show(eventCode, null); return true; },
                    ElementBounds.Fixed(EnumDialogArea.LeftFixed, 120, y + 6, 0, 0).WithFixedPadding(10, 4),
                    EnumButtonStyle.Small);
            }

            SingleComposer = composer.EndChildElements().Compose();
        }

        private double SectionHeader(GuiComposer composer, double y, string title, string hintKey)
        {
            y += 6;
            composer.AddStaticText(title, CairoFont.WhiteSmallText().WithColor(Palette.Header),
                ElementBounds.Fixed(0, y, Width, 20));
            y += 20;
            composer.AddStaticText(Lang.Get(hintKey), CairoFont.WhiteDetailText().WithColor(Palette.Detail),
                ElementBounds.Fixed(0, y, Width, 18));
            return y + 24;
        }

        // "[icon] Copper chain → [icon] Iron chain", level, price of the next step, buttons.
        private void ComposeUpgrade(GuiComposer composer, double y, ShopUpgradeMsg upgrade, int points)
        {
            var steps = upgrade.Steps ?? new UpgradeStepMsg[0];
            int level = Math.Min(upgrade.Level, steps.Length);
            string nowCode = level == 0 ? upgrade.Code : steps[level - 1].Code;
            int nowQty = level == 0 ? upgrade.Quantity : StepQuantity(steps[level - 1], upgrade.Quantity);

            var parts = new List<RichTextComponentBase>();
            AddItem(parts, nowCode, nowQty);
            var next = level < steps.Length ? steps[level] : null;
            if (next != null)
            {
                parts.Add(Text("  →  ", CairoFont.WhiteSmallText().WithColor(Palette.Detail)));
                AddItem(parts, next.Code, StepQuantity(next, upgrade.Quantity));
                // Icons only, the row has no room for more names.
                for (int i = 0; i < next.ExtraCount; i++) AddIcon(parts, next.ExtraCodes[i], next.ExtraQuantityAt(i));
            }
            composer.AddRichtext(parts.ToArray(), ElementBounds.Fixed(0, y, NameWidth, RowHeight - 2));

            composer.AddStaticText(Lang.Get("canevents:gui-shop-level", level, steps.Length),
                CairoFont.WhiteDetailText().WithColor(Palette.Detail), ElementBounds.Fixed(NameWidth + 6, y + 8, 60, 20));

            double bx = NameWidth + 70;
            if (next == null)
            {
                composer.AddStaticText(Lang.Get("canevents:gui-shop-maxed"),
                    CairoFont.WhiteDetailText().WithColor(Palette.Go), ElementBounds.Fixed(bx, y + 8, 70, 20));
            }
            else
            {
                composer.AddStaticText(Lang.Get("canevents:gui-shop-cost", next.Cost),
                    CairoFont.WhiteSmallText().WithColor(points >= next.Cost ? Palette.Title : Palette.Stop),
                    ElementBounds.Fixed(bx, y + 7, 60, 20));
                string id = upgrade.EntryId;
                composer.AddSmallButton(Lang.Get("canevents:gui-shop-upgrade"), () => Act(id, refund: false, upgrade: true),
                    ElementBounds.Fixed(EnumDialogArea.LeftFixed, bx + 60, y + 2, 0, 0).WithFixedPadding(8, 3),
                    EnumButtonStyle.Normal, "up-" + id);
            }

            if (upgrade.Refundable)
            {
                string id = upgrade.EntryId;
                composer.AddSmallButton(Lang.Get("canevents:gui-shop-refund"), () => Act(id, refund: true, upgrade: true),
                    ElementBounds.Fixed(EnumDialogArea.LeftFixed, bx + 155, y + 2, 0, 0).WithFixedPadding(8, 3),
                    EnumButtonStyle.Small, "down-" + id);
            }
        }

        private void ComposeOffer(GuiComposer composer, double y, ShopOfferMsg offer, int points)
        {
            var parts = new List<RichTextComponentBase>();
            AddItem(parts, offer.Code, offer.Quantity);
            composer.AddRichtext(parts.ToArray(), ElementBounds.Fixed(0, y, NameWidth, RowHeight - 2));

            composer.AddStaticText(offer.Bought + "/" + offer.MaxBuys, CairoFont.WhiteDetailText().WithColor(Palette.Detail),
                ElementBounds.Fixed(NameWidth + 6, y + 8, 60, 20));

            double bx = NameWidth + 70;
            if (offer.Bought >= offer.MaxBuys)
            {
                composer.AddStaticText(Lang.Get("canevents:gui-shop-maxed"),
                    CairoFont.WhiteDetailText().WithColor(Palette.Go), ElementBounds.Fixed(bx, y + 8, 70, 20));
                return;
            }

            composer.AddStaticText(Lang.Get("canevents:gui-shop-cost", offer.Cost),
                CairoFont.WhiteSmallText().WithColor(points >= offer.Cost ? Palette.Title : Palette.Stop),
                ElementBounds.Fixed(bx, y + 7, 60, 20));
            string id = offer.Id;
            composer.AddSmallButton(Lang.Get("canevents:gui-shop-buy"), () => Act(id, refund: false, upgrade: false),
                ElementBounds.Fixed(EnumDialogArea.LeftFixed, bx + 60, y + 2, 0, 0).WithFixedPadding(8, 3),
                EnumButtonStyle.Normal, "buy-" + id);
        }

        private static int StepQuantity(UpgradeStepMsg step, int kitQuantity) => step.Quantity > 0 ? step.Quantity : kitQuantity;

        private void AddItem(List<RichTextComponentBase> parts, string code, int quantity)
        {
            var stack = GuiItemUtil.Resolve(capi, code, Math.Max(1, quantity));
            if (stack != null) parts.Add(new ItemstackTextComponent(capi, stack, 28, 6, EnumFloat.Inline));
            parts.Add(Text((stack?.GetName() ?? code) + (quantity > 1 ? " ×" + quantity : ""), CairoFont.WhiteSmallText()));
        }

        private void AddIcon(List<RichTextComponentBase> parts, string code, int quantity)
        {
            var stack = GuiItemUtil.Resolve(capi, code, Math.Max(1, quantity));
            parts.Add(Text(" + ", CairoFont.WhiteSmallText().WithColor(Palette.Detail)));
            if (stack == null)
            {
                parts.Add(Text(code, CairoFont.WhiteDetailText()));
                return;
            }
            parts.Add(new ItemstackTextComponent(capi, stack, 28, 4, EnumFloat.Inline));
            if (quantity > 1) parts.Add(Text("×" + quantity, CairoFont.WhiteDetailText()));
        }

        private RichTextComponent Text(string text, CairoFont font)
            => new RichTextComponent(capi, text, font) { VerticalAlign = EnumVerticalAlign.Middle };

        private bool Act(string id, bool refund, bool upgrade)
        {
            networkHandler.SendShopAction(id, refund, upgrade);
            return true;
        }
    }
}
