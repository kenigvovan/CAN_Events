using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers;
using canevents.src.Gui.Admin.Widgets;
using canevents.src.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace canevents.src.Gui.Admin.Dialogs
{
    // Upgrade chains of one saved kit: pick an item, then its levels. Level 1 takes the item's place in
    // the kit, level 2 takes level 1's, and so on. Saves the whole set, the items themselves unchanged.
    public sealed class KitUpgradesPanel : CANGuiDialogPanel
    {
        private const int MaxLevels = 10;
        private const double InputHeight = 26;

        private const int MaxExtras = 8;

        private sealed class Extra
        {
            public string Code;
            public int Quantity;
        }

        private sealed class Level
        {
            public string Code;
            public int Quantity;
            public int Cost;
            public readonly List<Extra> Extras = new List<Extra>();
        }

        private readonly string eventCode;
        private readonly string setName;
        private readonly string[] codes;
        private readonly int[] quantities;
        private readonly string[] ids;
        private readonly List<Level>[] levels;
        private readonly Action<string[], UpgradeStepMsg[]> onSaved;
        private int selected;

        // Inputs to fill after compose: key -> value.
        private readonly Dictionary<string, string> inputValues = new Dictionary<string, string>();

        // onSaved: the item ids and steps that went out, so an open loadout editor can take them over.
        public KitUpgradesPanel(string eventCode, LoadoutSetMsg set, Action<string[], UpgradeStepMsg[]> onSaved = null)
        {
            this.eventCode = eventCode;
            this.onSaved = onSaved;
            setName = set.Name;
            codes = set.ItemCodes ?? new string[0];
            quantities = codes.Select((_, i) => set.ItemQuantities != null && i < set.ItemQuantities.Length ? set.ItemQuantities[i] : 1).ToArray();
            ids = codes.Select((_, i) => set.ItemIds != null && i < set.ItemIds.Length ? set.ItemIds[i] : null).ToArray();
            levels = codes.Select((_, i) => (set.Upgrades ?? new UpgradeStepMsg[0]).Where(u => u.Item == i)
                .Select(ToLevel).ToList()).ToArray();
            selected = Array.FindIndex(levels, l => l.Count > 0);
            if (selected < 0) selected = 0;
        }

        private static Level ToLevel(UpgradeStepMsg step)
        {
            var level = new Level { Code = step.Code, Quantity = step.Quantity, Cost = step.Cost };
            for (int i = 0; i < step.ExtraCount; i++)
                level.Extras.Add(new Extra { Code = step.ExtraCodes[i], Quantity = step.ExtraQuantityAt(i) });
            return level;
        }

        protected override string Title => Lang.Get("canevents:admin-kitupg-title", setName, eventCode);
        protected override double Width => 460;

        private ICoreClientAPI Capi => Gui.Capi;

        private string ItemName(string code)
            => string.IsNullOrEmpty(code) ? "?" : GuiItemUtil.Resolve(Capi, code)?.GetName() ?? code;

        protected override void BuildContent(DialogLayout l)
        {
            inputValues.Clear();
            Text(l, Lang.Get("canevents:admin-kitupg-hint"), AdminColors.Muted);

            if (codes.Length == 0)
            {
                TextRow(l, Lang.Get("canevents:admin-content-detail-empty"));
                Button(l, Lang.Get("canevents:admin-close"), () => { Close(); return true; });
                return;
            }

            TextRow(l, Lang.Get("canevents:admin-kitupg-items"), AdminColors.Label);
            for (int i = 0; i < codes.Length; i += 2)
            {
                var left = l.SplitRow(out var right, 4);
                ItemButton(l, left, i);
                if (i + 1 < codes.Length) ItemButton(l, right, i + 1);
            }

            BuildLevels(l);

            ConfirmDeclineRow(l, () =>
            {
                Save();
                return true;
            }, "canevents:admin-kitupg-save");
        }

        private void ItemButton(DialogLayout l, ElementBounds bounds, int index)
        {
            string caption = ItemName(codes[index]) + (quantities[index] > 1 ? " ×" + quantities[index] : "");
            if (levels[index].Count > 0) caption += "  [" + levels[index].Count + "]";
            if (index == selected) caption = "> " + caption;
            SmallButton(l, bounds, caption, () =>
            {
                selected = index;
                Gui.OpenSideDialog(this);
            });
        }

        private void BuildLevels(DialogLayout l)
        {
            var chain = levels[selected];
            TextRow(l, Lang.Get("canevents:admin-kitupg-levels", ItemName(codes[selected])), AdminColors.Section);

            if (chain.Count == 0)
            {
                TextRow(l, Lang.Get("canevents:admin-content-upg-none"), AdminColors.Muted);
            }
            else
            {
                var header = l.NextRow(6, height: 20);
                Cell(l, header, 0, 210, Lang.Get("canevents:admin-content-upg-col-level"));
                Cell(l, header, 214, 64, Lang.Get("canevents:admin-shop-col-cost"));
                Cell(l, header, 282, 64, Lang.Get("canevents:admin-content-col-qty"));

                for (int i = 0; i < chain.Count; i++) LevelRow(l, chain, i);
                TextRow(l, Lang.Get("canevents:admin-content-upg-qty-tip") + " " + Lang.Get("canevents:admin-kitupg-extra-tip"),
                    AdminColors.Muted);
            }

            if (chain.Count < MaxLevels)
            {
                Button(l, Lang.Get("canevents:admin-content-upg-add"), () =>
                {
                    string code = HeldCode();
                    if (code == null)
                    {
                        Gui.SetStatus(false, Lang.Get("canevents:admin-content-upg-need-held"));
                        return true;
                    }
                    chain.Add(new Level { Code = code, Cost = 1 });
                    Gui.OpenSideDialog(this);
                    return true;
                }, "kitupg-add");
            }
        }

        // "2. Iron chain" | cost | quantity | from hand | x (last level only).
        private void LevelRow(DialogLayout l, List<Level> chain, int index)
        {
            var level = chain[index];
            var row = l.NextRow(4, height: InputHeight);
            Cell(l, row, 0, 210, (index + 1) + ". " + ItemName(level.Code));

            string costKey = l.NextKey("cost");
            var costBounds = Child(l, row, 214, 60);
            l.Compo.AddTextInput(costBounds, text => level.Cost = Math.Clamp(ParseInt(text, level.Cost), 0, 100000),
                CairoFont.WhiteSmallText(), costKey);
            inputValues[costKey] = level.Cost.ToString();

            string qtyKey = l.NextKey("qty");
            var qtyBounds = Child(l, row, 282, 60);
            l.Compo.AddTextInput(qtyBounds, text => level.Quantity = Math.Clamp(ParseInt(text, level.Quantity), 0, 9999),
                CairoFont.WhiteSmallText(), qtyKey);
            inputValues[qtyKey] = level.Quantity.ToString();

            SmallButton(l, Child(l, row, 350, 52), Lang.Get("canevents:admin-kitupg-hand"), () =>
            {
                string code = HeldCode();
                if (code == null) return;
                level.Code = code;
                Gui.OpenSideDialog(this);
            });

            // Only the last one: a gap in the middle would renumber what players already bought.
            if (index == chain.Count - 1)
            {
                SmallButton(l, Child(l, row, 406, 26), "x", () =>
                {
                    chain.RemoveAt(index);
                    Gui.OpenSideDialog(this);
                });
            }

            foreach (var extra in level.Extras) ExtraRow(l, level, extra);

            if (level.Extras.Count < MaxExtras)
            {
                var addRow = l.NextRow(2, height: 22);
                SmallButton(l, Child(l, addRow, 20, 260, 22), Lang.Get("canevents:admin-kitupg-extra-add"), () =>
                {
                    string code = HeldCode();
                    if (code == null)
                    {
                        Gui.SetStatus(false, Lang.Get("canevents:admin-content-upg-need-held"));
                        return;
                    }
                    var stack = Capi.World.Player.InventoryManager.ActiveHotbarSlot.Itemstack;
                    level.Extras.Add(new Extra { Code = code, Quantity = Math.Max(1, stack?.StackSize ?? 1) });
                    Gui.OpenSideDialog(this);
                });
            }
        }

        // "+ Stones" | quantity | x; handed out with the level, gone with the next one.
        private void ExtraRow(DialogLayout l, Level level, Extra extra)
        {
            var row = l.NextRow(2, height: InputHeight);
            Cell(l, row, 20, 190, "+ " + ItemName(extra.Code));

            string qtyKey = l.NextKey("extra");
            l.Compo.AddTextInput(Child(l, row, 282, 60), text => extra.Quantity = Math.Clamp(ParseInt(text, extra.Quantity), 1, 9999),
                CairoFont.WhiteSmallText(), qtyKey);
            inputValues[qtyKey] = extra.Quantity.ToString();

            SmallButton(l, Child(l, row, 406, 26), "x", () =>
            {
                level.Extras.Remove(extra);
                Gui.OpenSideDialog(this);
            });
        }

        private static int ParseInt(string text, int fallback) => int.TryParse(text?.Trim(), out int v) ? v : fallback;

        private static void Cell(DialogLayout l, ElementBounds row, double x, double width, string text)
        {
            var font = CairoFont.WhiteDetailText();
            var bounds = Child(l, row, x, width, 20);
            bounds.fixedY += 4;
            l.Compo.AddStaticText(Card.Fit(text, font, width), font, bounds, l.NextKey("cell"));
        }

        private string HeldCode()
            => Capi?.World?.Player?.InventoryManager?.ActiveHotbarSlot?.Itemstack?.Collectible?.Code?.ToString();

        protected override void AfterCompose(GuiComposer compo)
        {
            foreach (var kv in inputValues) compo.GetTextInput(kv.Key)?.SetValue(kv.Value);
        }

        private void Save()
        {
            var steps = levels.SelectMany((chain, i) => chain.Select(s => new UpgradeStepMsg
            {
                Item = i,
                Code = s.Code,
                Quantity = s.Quantity,
                Cost = s.Cost,
                ExtraCodes = s.Extras.Count > 0 ? s.Extras.Select(e => e.Code).ToArray() : null,
                ExtraQuantities = s.Extras.Count > 0 ? s.Extras.Select(e => e.Quantity).ToArray() : null
            })).ToArray();
            var sentIds = ids.ToArray();

            Gui.Act(Lang.Get("canevents:admin-kitupg-log-save", eventCode, setName, steps.Length), () =>
            {
                Gui.Net.SendSaveLoadout(eventCode, setName, codes, quantities, ItemSetKinds.Loadout, sentIds, steps);
                Gui.Net.RequestShopConfig(eventCode);
            });
            onSaved?.Invoke(sentIds, steps);
            Close();
        }
    }
}
