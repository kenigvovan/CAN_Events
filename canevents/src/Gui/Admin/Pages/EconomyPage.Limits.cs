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
        private void BuildLimits(PageBuildContext ctx)
        {
            var compo = ctx.Compo;
            double top = ctx.Current.fixedY;
            double x0 = ctx.Main.fixedX;
            double width = ctx.Width;

            string hint = Lang.Get("canevents:admin-economy-lm-hint");
            double hintHeight = ButtonRow.TextHeight(hint, width);
            compo.AddStaticText(hint, AdminFonts.Hint, ElementBounds.Fixed(x0, top, width, hintHeight), "eco-lm-hint");
            double y = top + hintHeight + 6;

            double bx = x0;
            if (lm.IsLoaded)
            {
                string save = Lang.Get("canevents:admin-economy-lm-save");
                double w = ButtonRow.ButtonWidth(save);
                var b = ElementBounds.Fixed(bx, y, w, ButtonRow.ButtonHeight);
                compo.AddButton(save, new ActionConsumable(() =>
                {
                    var msg = lm.ToMsg();
                    lm.AcceptNext = true;
                    Act(Lang.Get("canevents:admin-economy-log-lm-save"), () => Net.SendSaveRewardLimits(msg));
                    return true;
                }), b, EnumButtonStyle.Normal, "eco-lm-save");
                Tooltip.Add(compo, Lang.Get("canevents:admin-economy-lm-save-hint"), b, "eco-lm-save-tip");
                bx += w + 8;
            }

            string reload = Lang.Get("canevents:admin-economy-reload");
            string reloadArmed = Lang.Get("canevents:admin-economy-reload-armed");
            Action doReload = () =>
            {
                lm.AcceptNext = true;
                Act(Lang.Get("canevents:admin-economy-log-lm-reload"), () => Net.SendRequestRewardLimits());
            };
            if (lm.IsDirty)
            {
                double w = ArmedTextButton.Width(reload, reloadArmed);
                ArmedTextButton.Add(ctx, ElementBounds.Fixed(bx, y, w, ButtonRow.ButtonHeight), "eco-lm-reload",
                    reload, doReload, reloadArmed, Lang.Get("canevents:admin-economy-lm-reload-hint"));
                bx += w + 10;
            }
            else
            {
                double w = ButtonRow.ButtonWidth(reload);
                var b = ElementBounds.Fixed(bx, y, w, ButtonRow.ButtonHeight);
                compo.AddButton(reload, new ActionConsumable(() => { doReload(); return true; }), b,
                    EnumButtonStyle.Normal, "eco-lm-reload");
                Tooltip.Add(compo, Lang.Get("canevents:admin-economy-lm-reload-hint"), b, "eco-lm-reload-tip");
                bx += w + 10;
            }

            string status = !lm.IsLoaded ? Lang.Get("canevents:admin-economy-loading-limits")
                : lm.IsDirty ? Lang.Get("canevents:admin-economy-lm-unsaved")
                : Lang.Get("canevents:admin-economy-lm-saved");
            AddFitText(compo, bx, y + 5, Math.Max(40, x0 + width - bx), status,
                lm.IsDirty ? AdminColors.Warning : AdminColors.Muted, "eco-lm-status");
            if (!lm.IsLoaded) return;

            y += ButtonRow.ButtonExtent + 4;

            var left = ctx.Column(0, LimitsTableWidth);
            var tableInner = Card.Frame(compo, left, y, ctx.Bottom - y, Lang.Get("canevents:admin-economy-lm-title"));
            SettingsTable.Add(ctx, tableInner, LimitsTableKey, LimitsRows());

            double rightX = LimitsTableWidth + Card.ColumnGap;
            BuildLimitsDefaultsCard(ctx, ctx.Column(rightX, width - rightX), y);
        }

        // Lower bounds match the clamps in LimitsEditorBuffer.ToMsg.
        private List<SettingRow> LimitsRows()
        {
            var b = lm;
            string codeTip = Lang.Get("canevents:admin-economy-lm-tip-code");
            return new List<SettingRow>
            {
                SettingRow.Group(Lang.Get("canevents:admin-economy-lm-group-ratecap")),
                SettingRow.Int("rateCapBurst", Lang.Get("canevents:admin-economy-lm-burst"),
                    () => b.RateBurst, v => b.RateBurst = v, 0,
                    tooltip: Lang.Get("canevents:admin-economy-lm-tip-burst")),
                SettingRow.Dbl("rateCapRefillHours", Lang.Get("canevents:admin-economy-lm-refill"),
                    () => b.RateRefillHours, v => b.RateRefillHours = v, 0.01,
                    tooltip: Lang.Get("canevents:admin-economy-lm-tip-refill")),
                SettingRow.Flag("rateCapPerEvent", Lang.Get("canevents:admin-economy-lm-per-event"),
                    () => b.RatePerEvent, v => b.RatePerEvent = v,
                    Lang.Get("canevents:admin-economy-lm-tip-per-event")),

                SettingRow.Group(Lang.Get("canevents:admin-economy-lm-group-throw")),
                SettingRow.Int("minMatchSeconds", Lang.Get("canevents:admin-economy-lm-minmatch"),
                    () => b.MinMatch, v => b.MinMatch = v, 0,
                    tooltip: Lang.Get("canevents:admin-economy-lm-tip-minmatch")),
                SettingRow.Flag("refundForfeitOnVoid", Lang.Get("canevents:admin-economy-lm-refund"),
                    () => b.RefundVoid, v => b.RefundVoid = v,
                    Lang.Get("canevents:admin-economy-lm-tip-refund")),

                SettingRow.Group(Lang.Get("canevents:admin-economy-lm-group-defaults")),
                SettingRow.Flag("participation", Lang.Get("canevents:admin-economy-lm-participation"),
                    () => b.Participation, v => b.Participation = v,
                    Lang.Get("canevents:admin-economy-lm-tip-participation")),
                SettingRow.Text("winnerItemCode", Lang.Get("canevents:admin-economy-lm-winner-code"),
                    () => b.WinnerCode, v => b.WinnerCode = (v ?? "").Trim(), tooltip: codeTip),
                SettingRow.Int("winnerQuantity", Lang.Get("canevents:admin-economy-lm-winner-qty"),
                    () => b.WinnerQty, v => b.WinnerQty = v, 0),
                SettingRow.Text("participationItemCode", Lang.Get("canevents:admin-economy-lm-part-code"),
                    () => b.PartCode, v => b.PartCode = (v ?? "").Trim(), tooltip: codeTip),
                SettingRow.Int("participationQuantity", Lang.Get("canevents:admin-economy-lm-part-qty"),
                    () => b.PartQty, v => b.PartQty = v, 0)
            };
        }

        private void BuildLimitsDefaultsCard(PageBuildContext ctx, ElementBounds column, double y)
        {
            var compo = ctx.Compo;
            var inner = Card.Frame(compo, column, y, ctx.Bottom - y, Lang.Get("canevents:admin-economy-defaults-title"));
            double width = inner.fixedWidth;
            double cy = inner.fixedY;

            string warn = Lang.Get("canevents:admin-economy-defaults-warn");
            double warnHeight = ButtonRow.TextHeight(warn, width);
            compo.AddStaticText(warn, CairoFont.WhiteSmallText().WithColor(AdminColors.Warning),
                ElementBounds.Fixed(inner.fixedX, cy, width, warnHeight), "eco-lmd-warn");
            cy += warnHeight + 2;

            string hint = Lang.Get("canevents:admin-economy-defaults-hint");
            double hintHeight = ButtonRow.TextHeight(hint, width);
            compo.AddStaticText(hint, AdminFonts.Hint, ElementBounds.Fixed(inner.fixedX, cy, width, hintHeight), "eco-lmd-hint");
            cy += hintHeight + 6;

            // Shows the buffers, not the server's copy.
            DefaultsTable(ctx, ElementBounds.Fixed(inner.fixedX, cy, width, 0), "eco-lmd",
                lm.WinnerCode, lm.WinnerQty, lm.PartCode, lm.PartQty, lm.Participation);
            cy += ColumnTable.HeaderHeight + 4 + (2 * 34 + 10) + 10;

            string heldWinner = Lang.Get("canevents:admin-economy-lm-held-winner");
            string heldPart = Lang.Get("canevents:admin-economy-lm-held-part");
            string heldHint = Lang.Get("canevents:admin-economy-lm-held-hint");
            var buttons = new ButtonRow(compo, ElementBounds.Fixed(inner.fixedX, cy, width, 0), width, 0, "eco-lmd-held");
            buttons.Add(heldWinner, () => TakeHeld(true), heldHint);
            buttons.Add(heldPart, () => TakeHeld(false), heldHint);
        }

        private void TakeHeld(bool winner)
        {
            if (!GuiItemUtil.TryReadHeldItem(Capi, out var code, out var qty))
            {
                Gui.SetStatus(false, Lang.Get("canevents:admin-economy-hold-first"));
                Gui.LogAction(false, Lang.Get("canevents:admin-economy-hold-first"));
                return;
            }
            qty = Math.Max(1, qty);
            if (winner) { lm.WinnerCode = code; lm.WinnerQty = qty; }
            else { lm.PartCode = code; lm.PartQty = qty; }

            // Edit line may hold the value just replaced.
            State.SettingsEditKey.Remove(LimitsTableKey);
            State.SettingsEditValue.Remove(LimitsTableKey);
            State.Drafts.Remove(LimitsTableKey + "-value");

            string which = Lang.Get(winner ? "canevents:admin-economy-def-winner" : "canevents:admin-economy-def-participation");
            Gui.LogAction(true, Lang.Get("canevents:admin-economy-log-held", which, code, qty));
            Rebuild();
        }
    }
}
