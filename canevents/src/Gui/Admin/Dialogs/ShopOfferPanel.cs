using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.Gui.Admin.Widgets;
using canevents.src.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace canevents.src.Gui.Admin.Dialogs
{
    // One one-time shop item. Edits a copy; onApply puts it into the shop tab's list, which Save sends.
    public sealed class ShopOfferPanel : CANGuiDialogPanel
    {
        private const double InputHeight = 26;
        private const string CodeKey = "shop-offer-code";

        private readonly ShopOfferConfigMsg offer;
        private readonly string[] kitNames;
        private readonly bool editing;
        private readonly Action<ShopOfferConfigMsg> onApply;
        private readonly Dictionary<string, string> inputValues = new Dictionary<string, string>();

        public ShopOfferPanel(ShopOfferConfigMsg offer, string[] kitNames, bool editing, Action<ShopOfferConfigMsg> onApply)
        {
            this.offer = offer;
            this.kitNames = kitNames;
            this.editing = editing;
            this.onApply = onApply;
        }

        protected override string Title => Lang.Get(editing ? "canevents:admin-shop-form-edit-title" : "canevents:admin-shop-form-new");
        protected override double Width => 380;

        protected override void BuildContent(DialogLayout l)
        {
            inputValues.Clear();
            Text(l, Lang.Get("canevents:admin-shop-item-hint"), AdminColors.Muted);

            TextRow(l, Lang.Get("canevents:admin-shop-item"), AdminColors.Label);
            var row = l.NextRow(4, height: InputHeight);
            var codeBounds = Child(l, row, 0, row.fixedWidth - 96);
            l.Compo.AddTextInput(codeBounds, text => offer.Code = text?.Trim(), CairoFont.WhiteSmallText(), CodeKey);
            inputValues[CodeKey] = offer.Code ?? "";
            SmallButton(l, Child(l, row, row.fixedWidth - 90, 90), Lang.Get("canevents:admin-shop-held"), () =>
            {
                var stack = Gui.Capi?.World?.Player?.InventoryManager?.ActiveHotbarSlot?.Itemstack;
                if (stack?.Collectible?.Code == null)
                {
                    Gui.SetStatus(false, Lang.Get("canevents:admin-content-upg-need-held"));
                    return;
                }
                offer.Code = stack.Collectible.Code.ToString();
                offer.Quantity = Math.Max(1, stack.StackSize);
                Gui.OpenSideDialog(this);
            });

            var labels = l.NextRow(8, height: 20);
            Cell(l, labels, 0, Lang.Get("canevents:admin-content-qty"));
            Cell(l, labels, 124, Lang.Get("canevents:admin-shop-col-cost"));
            Cell(l, labels, 248, Lang.Get("canevents:admin-shop-max"));
            var numbers = l.NextRow(2, height: InputHeight);
            NumberInput(l, numbers, 0, offer.Quantity, v => offer.Quantity = Math.Clamp(v, 1, 9999));
            NumberInput(l, numbers, 124, offer.Cost, v => offer.Cost = Math.Clamp(v, 0, 100000));
            NumberInput(l, numbers, 248, offer.MaxBuys, v => offer.MaxBuys = Math.Clamp(v, 1, 999));
            TextRow(l, Lang.Get("canevents:admin-shop-max-item-tip"), AdminColors.Muted);

            var dropRow = l.NextRow(8, height: InputHeight);
            SmallButton(l, Child(l, dropRow, 0, dropRow.fixedWidth),
                (offer.DropOnDeath ? "[x] " : "[ ] ") + Lang.Get("canevents:admin-shop-drop"), () =>
                {
                    offer.DropOnDeath = !offer.DropOnDeath;
                    Gui.OpenSideDialog(this);
                });

            TextRow(l, Lang.Get("canevents:admin-shop-kits"), AdminColors.Label);
            if (kitNames.Length == 0) TextRow(l, Lang.Get("canevents:admin-shop-no-kits"), AdminColors.Muted);
            for (int i = 0; i < kitNames.Length; i += 2)
            {
                var left = l.SplitRow(out var right, 4);
                KitToggle(l, left, kitNames[i]);
                if (i + 1 < kitNames.Length) KitToggle(l, right, kitNames[i + 1]);
            }

            ConfirmDeclineRow(l, () =>
            {
                if (string.IsNullOrWhiteSpace(offer.Code))
                {
                    Gui.SetStatus(false, Lang.Get("canevents:admin-shop-need-code"));
                    return true;
                }
                Close();
                onApply(offer);
                return true;
            }, editing ? "canevents:admin-shop-apply" : "canevents:admin-shop-add");
        }

        private void KitToggle(DialogLayout l, ElementBounds bounds, string kit)
        {
            bool on = offer.Kits != null && offer.Kits.Contains(kit);
            SmallButton(l, bounds, (on ? "[x] " : "[ ] ") + kit, () =>
            {
                var kits = (offer.Kits ?? new string[0]).Where(k => k != kit).ToList();
                if (!on) kits.Add(kit);
                offer.Kits = kits.ToArray();
                Gui.OpenSideDialog(this);
            });
        }

        private void NumberInput(DialogLayout l, ElementBounds row, double x, int value, Action<int> set)
        {
            string key = l.NextKey("num");
            l.Compo.AddTextInput(Child(l, row, x, 110), text =>
            {
                if (int.TryParse(text?.Trim(), out int v)) set(v);
            }, CairoFont.WhiteSmallText(), key);
            inputValues[key] = value.ToString();
        }

        private static void Cell(DialogLayout l, ElementBounds row, double x, string text)
        {
            var font = CairoFont.WhiteDetailText();
            var bounds = Child(l, row, x, 110, 20);
            l.Compo.AddStaticText(Card.Fit(text, font, 110), font, bounds, l.NextKey("cell"));
        }

        protected override void AfterCompose(GuiComposer compo)
        {
            foreach (var kv in inputValues) compo.GetTextInput(kv.Key)?.SetValue(kv.Value);
        }
    }
}
