using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace canevents.src.Gui.Native
{
    // Read-only look at an event's shop: every kit's upgrade chains and items, and how points come in.
    // Opened from the class pick, with B while queued, and from the match shop for the other classes.
    public class ShopPreviewGui : GuiDialog
    {
        private const double Width = 600;
        private const double MaxListHeight = 380;
        private const double ScrollbarWidth = 12;
        private const double Pad = 6;

        private readonly ClientNetworkHandler networkHandler;

        private ShopPreviewMsg preview;
        private string wantedKit;
        // null = all kits.
        private string kit;

        private double listHeight = MaxListHeight;
        private bool measuring;

        public ShopPreviewGui(ICoreClientAPI capi, ClientNetworkHandler networkHandler) : base(capi)
        {
            this.networkHandler = networkHandler;
        }

        public override string ToggleKeyCombinationCode => null;

        // kit: the class to start on, if the event has it.
        public void Show(string eventCode, string kit)
        {
            if (string.IsNullOrEmpty(eventCode)) return;
            wantedKit = kit;
            networkHandler.RequestShopPreview(eventCode);
        }

        // Wired to ClientNetworkHandler.OnShopPreview.
        public void OnPreview(ShopPreviewMsg msg)
        {
            preview = msg;
            var kits = msg.KitNames ?? new string[0];
            kit = wantedKit != null && kits.Contains(wantedKit) ? wantedKit : null;
            wantedKit = null;
            listHeight = MaxListHeight;
            Compose();
            TryOpen();
        }

        private void Compose()
        {
            if (preview == null) return;

            var composer = DialogShell.Begin(capi, "canevents-shoppreview",
                Lang.Get("canevents:gui-shoppreview-title", (preview.EventCode ?? "").ToUpperInvariant()), () => TryClose());
            double y = DialogShell.BodyTop;

            composer.AddStaticText(PointsText(), CairoFont.WhiteSmallText().WithColor(Palette.Title),
                ElementBounds.Fixed(0, y, Width, 40));
            y += 44;

            var kits = (preview.KitNames ?? new string[0])
                .Where(k => preview.Chains?.Any(c => c.Kit == k) == true
                            || preview.Offers?.Any(o => o.Kits != null && o.Kits.Contains(k)) == true)
                .ToArray();
            if (kits.Length > 0)
            {
                var values = new[] { "" }.Concat(kits).ToArray();
                var names = new[] { Lang.Get("canevents:gui-shoppreview-allkits") }.Concat(kits).ToArray();
                composer.AddStaticText(Lang.Get("canevents:gui-shoppreview-kit"), CairoFont.WhiteSmallText(),
                    ElementBounds.Fixed(0, y + 4, 70, 22));
                composer.AddDropDown(values, names, Math.Max(0, Array.IndexOf(values, kit ?? "")),
                    (value, selected) => { kit = string.IsNullOrEmpty(value) ? null : value; listHeight = MaxListHeight; Compose(); },
                    ElementBounds.Fixed(75, y, 240, 26), "kit");
                y += 36;
            }
            else kit = null;

            double textWidth = Width - ScrollbarWidth - 10 - Pad * 2;
            var clip = ElementBounds.Fixed(0, y, Width - ScrollbarWidth - 10, listHeight);
            var inset = clip.FlatCopy().FixedGrow(6).WithFixedOffset(-3, -3);
            var textBounds = ElementBounds.Fixed(Pad, Pad, textWidth, listHeight).WithParent(clip);
            var scrollbarBounds = ElementBounds.Fixed(Width - ScrollbarWidth, y, ScrollbarWidth, listHeight);

            composer.AddInset(inset, 3)
                .BeginClip(clip)
                .AddRichtext(BuildText(), textBounds, "list")
                .EndClip()
                .AddVerticalScrollbar(value =>
                {
                    var text = SingleComposer?.GetRichtext("list");
                    if (text == null) return;
                    text.Bounds.fixedY = Pad - value;
                    text.Bounds.CalcWorldBounds();
                }, scrollbarBounds, "scroll");
            y += listHeight + 12;

            composer.AddSmallButton(Lang.Get("canevents:gui-shop-close"), () => TryClose(),
                ElementBounds.Fixed(EnumDialogArea.LeftFixed, 0, y, 0, 0).WithFixedPadding(10, 4),
                EnumButtonStyle.Normal);

            SingleComposer = composer.EndChildElements().Compose();

            var richtext = SingleComposer.GetRichtext("list");
            double content = (richtext?.Bounds.fixedHeight ?? 0) + Pad * 2;
            SingleComposer.GetScrollbar("scroll")?.SetHeights((float)listHeight, (float)content);

            // Shrink to short lists; one more pass settles it.
            double wanted = Math.Clamp(content, 60, MaxListHeight);
            if (!measuring && Math.Abs(wanted - listHeight) > 0.5)
            {
                listHeight = wanted;
                measuring = true;
                Compose();
                measuring = false;
            }
        }

        private string PointsText()
        {
            var parts = new List<string>();
            if (preview.StartPoints > 0) parts.Add(Lang.Get("canevents:gui-shoppreview-start", preview.StartPoints));
            if (preview.PointsPerKill != 0) parts.Add(Lang.Get("canevents:gui-shoppreview-kill", Signed(preview.PointsPerKill)));
            if (preview.PointsPerDeath != 0) parts.Add(Lang.Get("canevents:gui-shoppreview-death", Signed(preview.PointsPerDeath)));
            if (preview.PointsPerRoundWin != 0) parts.Add(Lang.Get("canevents:gui-shoppreview-round", Signed(preview.PointsPerRoundWin)));

            string where = preview.BuyAnywhere
                ? Lang.Get("canevents:gui-shoppreview-anywhere")
                : Lang.Get("canevents:gui-shop-where", preview.BuyRadius);
            return string.Join(" · ", parts) + "\n" + where;
        }

        private static string Signed(int n) => n > 0 ? "+" + n : n.ToString();

        private bool Fits(ShopOfferConfigMsg o)
            => kit == null || o.Kits == null || o.Kits.Length == 0 || o.Kits.Contains(kit);

        private RichTextComponentBase[] BuildText()
        {
            var parts = new List<RichTextComponentBase>();
            var chains = (preview.Chains ?? new KitChainMsg[0]).Where(c => kit == null || c.Kit == kit).ToArray();
            var offers = (preview.Offers ?? new ShopOfferConfigMsg[0]).Where(Fits).ToArray();

            if (chains.Length == 0 && offers.Length == 0)
            {
                parts.Add(Text(Lang.Get("canevents:gui-shoppreview-empty"), Detail()));
                return parts.ToArray();
            }

            if (chains.Length > 0)
            {
                Header(parts, "canevents:gui-shoppreview-upgrades", "canevents:gui-shop-upgrades-hint");
                string lastKit = null;
                foreach (var chain in chains)
                {
                    if (kit == null && chain.Kit != lastKit)
                    {
                        parts.Add(Text(chain.Kit + "\r\n", CairoFont.WhiteSmallText().WithColor(Palette.Title)));
                        lastKit = chain.Kit;
                    }
                    AddChain(parts, chain);
                }
            }

            if (offers.Length > 0)
            {
                if (chains.Length > 0) parts.Add(Text("\r\n", Detail()));
                Header(parts, "canevents:gui-shop-items", "canevents:gui-shop-items-hint");
                foreach (var o in offers) AddOffer(parts, o);
            }

            return parts.ToArray();
        }

        // The kit item, then one indented line per level.
        private void AddChain(List<RichTextComponentBase> parts, KitChainMsg chain)
        {
            AddItem(parts, chain.Code, chain.Quantity);
            parts.Add(Text("   " + Lang.Get("canevents:gui-shoppreview-in-kit") + "\r\n", Detail()));

            var steps = chain.Steps ?? new UpgradeStepMsg[0];
            for (int i = 0; i < steps.Length; i++)
            {
                parts.Add(Text(new string(' ', 6) + (i + 1) + ". → ", Detail()));
                AddItem(parts, steps[i].Code, steps[i].Quantity > 0 ? steps[i].Quantity : chain.Quantity);
                for (int e = 0; e < steps[i].ExtraCount; e++)
                {
                    parts.Add(Text(" + ", Detail()));
                    AddItem(parts, steps[i].ExtraCodes[e], steps[i].ExtraQuantityAt(e));
                }
                parts.Add(Text("  " + Lang.Get("canevents:gui-shop-cost", steps[i].Cost) + "\r\n",
                    CairoFont.WhiteSmallText().WithColor(Palette.Title)));
            }
        }

        private void AddOffer(List<RichTextComponentBase> parts, ShopOfferConfigMsg o)
        {
            AddItem(parts, o.Code, o.Quantity);
            parts.Add(Text("  " + Lang.Get("canevents:gui-shop-cost", o.Cost), CairoFont.WhiteSmallText().WithColor(Palette.Title)));

            var notes = new List<string>();
            if (o.MaxBuys > 1) notes.Add(Lang.Get("canevents:gui-shoppreview-max-life", o.MaxBuys));
            if (o.DropOnDeath) notes.Add(Lang.Get("canevents:gui-shoppreview-drops"));
            if (kit == null && o.Kits != null && o.Kits.Length > 0) notes.Add(string.Join(", ", o.Kits));

            if (notes.Count > 0) parts.Add(Text("   " + string.Join(" · ", notes), Detail()));
            parts.Add(Text("\r\n", Detail()));
        }

        private void AddItem(List<RichTextComponentBase> parts, string code, int quantity)
        {
            var stack = GuiItemUtil.Resolve(capi, code, Math.Max(1, quantity));
            if (stack != null) parts.Add(new ItemstackTextComponent(capi, stack, 28, 6, EnumFloat.Inline));
            parts.Add(Text((stack?.GetName() ?? code) + (quantity > 1 ? " ×" + quantity : ""), CairoFont.WhiteSmallText()));
        }

        private void Header(List<RichTextComponentBase> parts, string titleKey, string hintKey)
        {
            parts.Add(Text(Lang.Get(titleKey) + "\r\n", CairoFont.WhiteSmallishText().WithColor(Palette.Header)));
            parts.Add(Text(Lang.Get(hintKey) + "\r\n", Detail()));
        }

        private RichTextComponent Text(string text, CairoFont font)
            => new RichTextComponent(capi, text, font) { VerticalAlign = EnumVerticalAlign.Middle };

        private static CairoFont Detail() => CairoFont.WhiteDetailText().WithColor(Palette.Detail);
    }
}
