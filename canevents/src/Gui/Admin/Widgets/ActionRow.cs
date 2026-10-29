using System;
using Vintagestory.API.Client;

namespace canevents.src.Gui.Admin.Widgets
{
    // Icon buttons at the bottom of a card, laid out left to right.
    public sealed class ActionRow
    {
        private readonly GuiComposer compo;
        private ElementBounds slot;

        public ActionRow(GuiComposer compo, ElementBounds firstSlot)
        {
            this.compo = compo;
            this.slot = firstSlot;
        }

        public ElementBounds Add(string icon, string key, Action<bool> onClick, string tooltip, bool toggleable = false)
        {
            ElementBounds bounds = slot;

            compo.AddIconButton(icon, onClick, bounds, key);
            if (toggleable) compo.GetToggleButton(key).Toggleable = true;
            if (tooltip != null) Tooltip.Add(compo, tooltip, bounds, "tip-" + key);

            slot = Card.NextAction(bounds);
            return bounds;
        }
    }
}
