using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers.events;
using canevents.src.Gui.Admin.Dialogs;
using canevents.src.Gui.Admin.GuiElements;
using canevents.src.Gui.Admin.Widgets;
using canevents.src.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace canevents.src.Gui.Admin.Pages
{
    public sealed partial class EconomyPage
    {
        private void BuildItemSetsCard(PageBuildContext ctx, RewardLimitsMsg limits, double y)
        {
            var compo = ctx.Compo;
            var column = ctx.Column(0, LeftWidth);
            double height = ctx.Bottom - y;
            var inner = Card.Frame(compo, column, y, height, Lang.Get("canevents:admin-economy-sets-title"));
            double width = inner.fixedWidth;
            double bottom = inner.fixedY + inner.fixedHeight;

            if (rw.SetAt(rw.SelectedSet) == null) rw.SelectedSet = RewardEditorBuffer.SetWinner;
            int selected = rw.SelectedSet;

            var captions = new List<string>
            {
                Lang.Get("canevents:admin-economy-set-winner"),
                Lang.Get("canevents:admin-economy-set-participation"),
                Lang.Get("canevents:admin-economy-set-fee")
            };
            for (int i = 0; i < rw.Tiers.Count; i++) captions.Add(Lang.Get("canevents:admin-economy-set-place", i + 1));
            for (int i = 0; i < captions.Count; i++)
                if (i == selected) captions[i] = "> " + captions[i] + " <";
            string addPlace = Lang.Get("canevents:admin-economy-set-add-place", rw.Tiers.Count + 1);

            var all = new List<string>(captions) { addPlace };
            double selectorHeight = ButtonRow.HeightFor(width, 0, all.ToArray());
            var selector = new ButtonRow(compo, ElementBounds.Fixed(inner.fixedX, inner.fixedY, width, 0), width, 0, "eco-set");
            for (int i = 0; i < captions.Count; i++)
            {
                int index = i;
                selector.Add(captions[i], () =>
                {
                    if (rw.SelectedSet == index) return;
                    rw.SelectedSet = index;
                    State.ArmedKey = null;
                    Rebuild();
                }, SetTooltip(index));
            }
            selector.Add(addPlace, () =>
            {
                rw.Tiers.Add(new List<RewardItem>());
                rw.SelectedSet = RewardEditorBuffer.FirstTierSet + rw.Tiers.Count - 1;
                Gui.LogAction(true, Lang.Get("canevents:admin-economy-log-add-place", rw.Tiers.Count, rw.EventCode));
                Rebuild();
            }, Lang.Get("canevents:admin-economy-set-add-place-hint"));

            double cy = inner.fixedY + selectorHeight + 4;

            string description = SetDescription(selected);
            double descHeight = ButtonRow.TextHeight(description, width);
            compo.AddStaticText(description, AdminFonts.Hint, ElementBounds.Fixed(inner.fixedX, cy, width, descHeight), "eco-set-desc");
            cy += descHeight + 2;

            var (note, noteColor) = SetNote(selected);
            if (note != null)
            {
                double noteHeight = ButtonRow.TextHeight(note, width);
                compo.AddStaticText(note, CairoFont.WhiteSmallText().WithColor(noteColor),
                    ElementBounds.Fixed(inner.fixedX, cy, width, noteHeight), "eco-set-note");
                cy += noteHeight + 2;
            }
            cy += 4;

            var items = rw.SetAt(selected);
            var opts = new ItemListEditorOptions
            {
                Key = "eco-items",
                LogName = rw.EventCode + " / " + StripMarks(captions[selected])
            };

            // Empty set falls back to the server default; show it.
            if (selected == RewardEditorBuffer.SetWinner || selected == RewardEditorBuffer.SetParticipation)
            {
                string fbCode = selected == RewardEditorBuffer.SetWinner ? limits?.WinnerItemCode : limits?.ParticipationItemCode;
                int fbQty = selected == RewardEditorBuffer.SetWinner ? limits?.WinnerQuantity ?? 0 : limits?.ParticipationQuantity ?? 0;
                if (!string.IsNullOrWhiteSpace(fbCode) && fbQty > 0)
                {
                    opts.FallbackCode = fbCode;
                    opts.FallbackQuantity = fbQty;
                    opts.FallbackTooltip = Lang.Get("canevents:admin-economy-inherit-default");
                }
                else
                {
                    opts.EmptyText = limits == null
                        ? Lang.Get("canevents:admin-economy-inherit-unknown")
                        : Lang.Get("canevents:admin-economy-inherit-none");
                    opts.EmptyColor = AdminColors.Warning;
                }
            }
            else if (selected == RewardEditorBuffer.SetFee)
            {
                opts.EmptyText = Lang.Get("canevents:admin-economy-fee-free");
            }
            else
            {
                int tier = selected - RewardEditorBuffer.FirstTierSet;
                opts.EmptyText = Lang.Get("canevents:admin-economy-place-empty");
                opts.Extra.Add(new ItemListButton
                {
                    Caption = Lang.Get("canevents:admin-economy-remove-place"),
                    SecondClick = true,
                    ArmedCaption = Lang.Get("canevents:admin-economy-remove-place-armed"),
                    Tooltip = Lang.Get("canevents:admin-economy-remove-place-hint"),
                    OnClick = () =>
                    {
                        if (tier < 0 || tier >= rw.Tiers.Count) return;
                        rw.Tiers.RemoveAt(tier);
                        rw.SelectedSet = rw.Tiers.Count > 0
                            ? RewardEditorBuffer.FirstTierSet + Math.Min(tier, rw.Tiers.Count - 1)
                            : RewardEditorBuffer.SetWinner;
                        Gui.LogAction(true, Lang.Get("canevents:admin-economy-log-remove-place", tier + 1, rw.EventCode));
                    }
                });
            }

            ItemListEditor.Add(ctx, ElementBounds.Fixed(inner.fixedX, cy, width, Math.Max(120, bottom - cy)),
                items, rw.Input, opts);
        }

        private static string StripMarks(string caption)
            => caption.StartsWith("> ") && caption.EndsWith(" <") ? caption.Substring(2, caption.Length - 4) : caption;

        private string SetTooltip(int index)
        {
            var items = rw.SetAt(index);
            int count = items?.Count ?? 0;
            return count == 0
                ? Lang.Get("canevents:admin-economy-set-tip-empty")
                : Lang.Get("canevents:admin-economy-set-tip-count", count);
        }

        private string SetDescription(int index)
        {
            switch (index)
            {
                case RewardEditorBuffer.SetWinner: return Lang.Get("canevents:admin-economy-set-winner-hint");
                case RewardEditorBuffer.SetParticipation: return Lang.Get("canevents:admin-economy-set-participation-hint");
                case RewardEditorBuffer.SetFee: return Lang.Get("canevents:admin-economy-set-fee-hint");
                default: return Lang.Get("canevents:admin-economy-set-place-hint", index - RewardEditorBuffer.FirstTierSet + 1);
            }
        }

        private (string text, double[] color) SetNote(int index)
        {
            switch (index)
            {
                case RewardEditorBuffer.SetWinner:
                    // Any placement tier silently disables the winner set.
                    return rw.Tiers.Count > 0
                        ? (Lang.Get("canevents:admin-economy-places-active"), AdminColors.Warning)
                        : (Lang.Get("canevents:admin-economy-places-none"), AdminColors.Muted);
                case RewardEditorBuffer.SetParticipation:
                    return rw.ParticipationMode == 2
                        ? (Lang.Get("canevents:admin-economy-participation-off"), AdminColors.Warning)
                        : (null, null);
                case RewardEditorBuffer.SetFee:
                    return (FeeSummary(), AdminColors.Muted);
                default:
                    return (Lang.Get("canevents:admin-economy-places-active"), AdminColors.Warning);
            }
        }

        private string FeeSummary()
        {
            int rake = Math.Clamp(rw.Rake, 0, 100);
            if (rw.Fee.Count == 0) return Lang.Get("canevents:admin-economy-fee-summary-free");
            return rake == 0
                ? Lang.Get("canevents:admin-economy-fee-summary-all")
                : Lang.Get("canevents:admin-economy-fee-summary-split", 100 - rake);
        }
    }
}
