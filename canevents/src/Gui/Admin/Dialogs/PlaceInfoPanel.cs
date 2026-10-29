using System.Collections.Generic;
using System.Linq;
using canevents.src.EventPlacesManagers.Places;
using canevents.src.Gui.Admin.Widgets;
using canevents.src.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace canevents.src.Gui.Admin.Dialogs
{
    // Typed text lives in the form object so toggling a tag (which rebuilds) keeps it.
    public sealed class PlaceInfoPanel : CANGuiDialogPanel
    {
        private const string NameKey = "place-info-name";
        private const string DescKey = "place-info-desc";

        private readonly long placeId;
        private string name;
        private string description;
        private readonly HashSet<string> tags;

        public PlaceInfoPanel(PlaceListEntryMsg place)
        {
            placeId = place.Id;
            name = place.Name ?? "";
            description = place.Description ?? "";
            tags = new HashSet<string>(place.Types ?? new string[0]);
        }

        // Known tags, then any tag a loaded event asks for (BATTLE, TDMOPEN...), then the place's own.
        private List<string> TagOrder()
        {
            var eventTags = Gui.Net.AdminEventList.Value?.Events?
                .Where(e => e?.EventPlaceTypes != null)
                .SelectMany(e => e.EventPlaceTypes)
                ?? Enumerable.Empty<string>();
            return PlaceTags.Known.Concat(eventTags).Concat(tags)
                .Where(t => !string.IsNullOrEmpty(t))
                .Distinct()
                .ToList();
        }

        protected override string Title => Lang.Get("canevents:admin-place-info-title", placeId);
        protected override double Width => 320;

        protected override void BuildContent(DialogLayout l)
        {
            Text(l, Lang.Get("canevents:admin-place-info-name"), AdminColors.Label);
            l.Compo.AddTextInput(l.NextRow(4), text => name = text ?? "", CairoFont.WhiteSmallText(), NameKey);

            TextRow(l, Lang.Get("canevents:admin-place-info-desc"), AdminColors.Label);
            l.Compo.AddTextInput(l.NextRow(4), text => description = text ?? "", CairoFont.WhiteSmallText(), DescKey);

            TextRow(l, Lang.Get("canevents:admin-place-info-tags"), AdminColors.Label);
            var tagOrder = TagOrder();
            for (int i = 0; i < tagOrder.Count; i += 2)
            {
                var left = l.SplitRow(out var right, 4);
                AddTagButton(l, left, tagOrder[i]);
                if (i + 1 < tagOrder.Count) AddTagButton(l, right, tagOrder[i + 1]);
            }

            ConfirmDeclineRow(l, () =>
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    Gui.SetStatus(false, Lang.Get("canevents:place-name-empty"));
                    return true;
                }
                string newName = name, newDescription = description;
                var newTags = tags.ToArray();
                Gui.Act(Lang.Get("canevents:admin-arenas-log-edit-info", newName),
                    () => Gui.Net.SendEditPlaceInfo(placeId, newName, newDescription, newTags));
                Close();
                return true;
            }, "canevents:admin-place-info-save");
        }

        private void AddTagButton(DialogLayout l, ElementBounds bounds, string tag)
        {
            string caption = (tags.Contains(tag) ? "[x] " : "[ ] ") + tag;
            // GuiElementTextButton directly: AddButton draws the big ButtonText font whatever the style,
            // and long tags spilled into the next button.
            var font = CairoFont.SmallButtonText(EnumButtonStyle.Small);
            var pressed = CairoFont.SmallButtonText(EnumButtonStyle.Small);
            pressed.Color = (double[])GuiStyle.ActiveButtonTextColor.Clone();
            l.Compo.AddInteractiveElement(new GuiElementTextButton(l.Compo.Api, caption, font, pressed,
                new ActionConsumable(() =>
                {
                    if (!tags.Remove(tag)) tags.Add(tag);
                    Gui.OpenSideDialog(this);
                    return true;
                }), bounds, EnumButtonStyle.Small), l.NextKey("tag"));
        }

        protected override void AfterCompose(GuiComposer compo)
        {
            compo.GetTextInput(NameKey)?.SetValue(name);
            compo.GetTextInput(DescKey)?.SetValue(description);
        }
    }
}
