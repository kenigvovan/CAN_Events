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
    // Both editors buffer a server snapshot and send it all back on Save.
    // Default reward travels in the limits packet, so it's edited on Limits and read-only on Rewards.
    public sealed partial class EconomyPage : AdminPageBase
    {
        private const int SubRewards = 0, SubLimits = 1;

        private const double LineHeight = 20;
        private const double InputHeight = 26;
        private const double TopBarHeight = 36;
        private const double LeftWidth = 520;
        private const double SettingPitch = 30;
        private const double SettingLabelWidth = 160;
        private const double LimitsTableWidth = 560;

        private const string LimitsTableKey = "eco-lm";

        private readonly RewardEditorBuffer rw = new RewardEditorBuffer();
        private readonly LimitsEditorBuffer lm = new LimitsEditorBuffer();

        protected override PanelTab Tab => PanelTab.Economy;

        protected override string[] SubTabLangKeys => new[]
        {
            "canevents:admin-economy-subtab-rewards",
            "canevents:admin-economy-subtab-limits"
        };

        public override void Watch(PageWatchList watch)
        {
            watch.Add(Net.AdminEventList);
            watch.Add(Net.EventRewardConfig);
            watch.Add(Net.RewardLimits);
        }

        // Both sub-tabs need global limits (Rewards shows what "inherit" resolves to).
        public override void OnShown() => Net.SendRequestRewardLimits();

        protected override void OnSubTabChanged(int subTab)
        {
            if (subTab == SubRewards) Net.RequestAdminEventList();
            Net.SendRequestRewardLimits();
        }

        protected override void BuildSubTab(PageBuildContext ctx, int subTab)
        {
            SyncLimits();
            if (subTab == SubLimits) BuildLimits(ctx);
            else BuildRewards(ctx);
        }

        private void SyncLimits()
        {
            var lim = Net.RewardLimits.Value;
            if (lim == null || ReferenceEquals(lm.LoadedFrom, lim)) return;
            // Unsaved edits survive a refresh, only Save/Reload replace them.
            if (lm.IsDirty && !lm.AcceptNext) return;
            lm.AcceptNext = false;
            lm.Load(lim);
            State.SettingsEditKey.Remove(LimitsTableKey);
            State.SettingsEditValue.Remove(LimitsTableKey);
            State.Drafts.Remove(LimitsTableKey + "-value");
        }

        private void BuildRewards(PageBuildContext ctx)
        {
            var compo = ctx.Compo;
            double top = ctx.Current.fixedY;
            double x0 = ctx.Main.fixedX;

            string[] codes = EventCodes();
            if (codes.Length == 0)
            {
                AddText(compo, x0, top + 4, 300, Lang.Get("canevents:admin-economy-no-events"), AdminColors.Muted, "eco-noev");
                string refresh = Lang.Get("canevents:admin-economy-refresh");
                var b = ElementBounds.Fixed(x0 + 310, top, ButtonRow.ButtonWidth(refresh), ButtonRow.ButtonHeight);
                compo.AddButton(refresh, new ActionConsumable(() => { Net.RequestAdminEventList(); return true; }),
                    b, EnumButtonStyle.Normal, "eco-noev-refresh");
                return;
            }

            if (string.IsNullOrEmpty(rw.EventCode) || Array.IndexOf(codes, rw.EventCode) < 0) rw.EventCode = codes[0];

            // Once per event; a save reply also carries the stored config.
            if (rw.EventCode != rw.Requested)
            {
                Net.SendRequestEventRewardConfig(rw.EventCode);
                rw.Requested = rw.EventCode;
            }

            var cfg = Net.EventRewardConfig.Value;
            if (cfg != null && cfg.EventCode == rw.EventCode && !ReferenceEquals(rw.LoadedFrom, cfg))
            {
                rw.Load(cfg);
                State.Drafts.Remove("eco-rake");
                State.Drafts.Remove("eco-minmatch");
            }

            var limits = Net.RewardLimits.Value;
            BuildRewardsTopBar(ctx, codes, top);
            if (!rw.IsLoaded) return;

            double y = top + TopBarHeight;
            BuildItemSetsCard(ctx, limits, y);

            double rightX = LeftWidth + Card.ColumnGap;
            var right = ctx.Column(rightX, ctx.Width - rightX);
            double nextY = BuildPayoutSettingsCard(ctx, right, limits, y);
            BuildDefaultsCard(ctx, right, limits, nextY);
        }

        private string[] EventCodes()
        {
            var list = Net.AdminEventList.Value;
            return list?.Events != null
                ? list.Events.Where(e => !string.IsNullOrEmpty(e.Code)).Select(e => e.Code).ToArray()
                : Array.Empty<string>();
        }

        private void BuildRewardsTopBar(PageBuildContext ctx, string[] codes, double top)
        {
            var compo = ctx.Compo;
            double x0 = ctx.Main.fixedX;
            string code = rw.EventCode;

            string label = Lang.Get("canevents:admin-economy-event");
            double labelWidth = CairoFont.WhiteSmallText().GetTextExtents(label).Width + 10;
            AddText(compo, x0, top + 5, labelWidth, label, AdminColors.Label, "eco-ev-label");

            var dropBounds = ElementBounds.Fixed(x0 + labelWidth, top, 230, InputHeight);
            compo.AddDropDown(codes, codes, Math.Max(0, Array.IndexOf(codes, code)), (picked, on) =>
            {
                if (string.IsNullOrEmpty(picked) || picked == rw.EventCode) return;
                if (rw.IsDirty)
                {
                    // Switching reloads buffers, confirm to not lose edits.
                    string from = rw.EventCode;
                    Gui.OpenSideDialog(new ConfirmPanel(Lang.Get("canevents:admin-economy-dirty-title"),
                        Lang.Get("canevents:admin-economy-dirty-question", from, picked),
                        () => SwitchEvent(picked)));
                    Rebuild(); // reset the dropdown to the current event
                    return;
                }
                SwitchEvent(picked);
            }, dropBounds, "eco-ev-pick");
            Tooltip.Add(compo, Lang.Get("canevents:admin-economy-event-hint"), dropBounds, "eco-ev-pick-tip");

            double bx = dropBounds.fixedX + dropBounds.fixedWidth + 10;
            string reload = Lang.Get("canevents:admin-economy-reload");
            string reloadArmed = Lang.Get("canevents:admin-economy-reload-armed");
            Action doReload = () =>
            {
                rw.Requested = null; // re-asked on the rebuild
                Gui.LogAction(true, Lang.Get("canevents:admin-economy-log-reload", code));
                Rebuild();
            };
            if (rw.IsDirty)
            {
                double w = ArmedTextButton.Width(reload, reloadArmed);
                ArmedTextButton.Add(ctx, ElementBounds.Fixed(bx, top, w, ButtonRow.ButtonHeight), "eco-reload",
                    reload, doReload, reloadArmed, Lang.Get("canevents:admin-economy-reload-hint"));
                bx += w + 8;
            }
            else
            {
                double w = ButtonRow.ButtonWidth(reload);
                var b = ElementBounds.Fixed(bx, top, w, ButtonRow.ButtonHeight);
                compo.AddButton(reload, new ActionConsumable(() => { doReload(); return true; }), b,
                    EnumButtonStyle.Normal, "eco-reload");
                Tooltip.Add(compo, Lang.Get("canevents:admin-economy-reload-hint"), b, "eco-reload-tip");
                bx += w + 8;
            }

            if (rw.IsLoaded)
            {
                string save = Lang.Get("canevents:admin-economy-save");
                double w = ButtonRow.ButtonWidth(save);
                var b = ElementBounds.Fixed(bx, top, w, ButtonRow.ButtonHeight);
                compo.AddButton(save, new ActionConsumable(() =>
                {
                    var msg = rw.ToMsg();
                    Act(Lang.Get("canevents:admin-economy-log-save", msg.EventCode), () => Net.SendSaveEventRewardConfig(msg));
                    return true;
                }), b, EnumButtonStyle.Normal, "eco-save");
                Tooltip.Add(compo, Lang.Get("canevents:admin-economy-save-hint", code), b, "eco-save-tip");
                bx += w + 10;
            }

            string status;
            double[] color;
            if (!rw.IsLoaded) { status = Lang.Get("canevents:admin-economy-loading", code); color = AdminColors.Muted; }
            else if (rw.IsDirty) { status = Lang.Get("canevents:admin-economy-unsaved", code); color = AdminColors.Warning; }
            else { status = Lang.Get("canevents:admin-economy-saved", code); color = AdminColors.Muted; }
            double statusWidth = Math.Max(40, x0 + ctx.Width - bx);
            AddFitText(compo, bx, top + 5, statusWidth, status, color, "eco-status");
        }

        private void SwitchEvent(string code)
        {
            rw.EventCode = code;
            rw.SelectedSet = RewardEditorBuffer.SetWinner;
            State.ArmedKey = null;
            Rebuild();
        }

        private double BuildPayoutSettingsCard(PageBuildContext ctx, ElementBounds column, RewardLimitsMsg limits, double y)
        {
            var compo = ctx.Compo;
            const int rowsCount = 7;
            double height = Card.HeightAround(rowsCount * SettingPitch);
            var inner = Card.Frame(compo, column, y, height, Lang.Get("canevents:admin-economy-settings-title"));
            double controlX = inner.fixedX + SettingLabelWidth + 6;
            double controlWidth = inner.fixedWidth - SettingLabelWidth - 6;
            double cy = inner.fixedY;

            SettingLabel(compo, inner, cy, "eco-part", Lang.Get("canevents:admin-economy-row-participation"),
                Lang.Get("canevents:admin-economy-tip-participation"), rw.ParticipationMode != 0);
            TriDropDown(ctx, ElementBounds.Fixed(controlX, cy, controlWidth, InputHeight), "eco-part-dd",
                Lang.Get("canevents:admin-economy-row-participation"), rw.ParticipationMode, limits?.Participation,
                v => rw.ParticipationMode = v);
            cy += SettingPitch;

            SettingLabel(compo, inner, cy, "eco-rake", Lang.Get("canevents:admin-economy-row-rake"),
                Lang.Get("canevents:admin-economy-tip-rake"), false);
            NumberField.AddInt(ctx, ElementBounds.Fixed(controlX, cy, 64, InputHeight), "eco-rake",
                () => rw.Rake, v => rw.Rake = v, 0, 100);
            cy += SettingPitch;
            AddFitText(compo, inner.fixedX + 10, cy + 3, inner.fixedWidth - 10, FeeSummary(), AdminColors.Muted, "eco-fee-summary");
            cy += SettingPitch;

            SettingLabel(compo, inner, cy, "eco-mm", Lang.Get("canevents:admin-economy-row-minmatch"),
                Lang.Get("canevents:admin-economy-tip-minmatch"), !rw.MinMatchInherit);
            string globalLabel = limits == null
                ? Lang.Get("canevents:admin-economy-minmatch-global")
                : Lang.Get("canevents:admin-economy-minmatch-global-value", limits.MinMatchSeconds > 0
                    ? limits.MinMatchSeconds + "s"
                    : Lang.Get("canevents:admin-economy-minmatch-none"));
            string own = Lang.Get("canevents:admin-economy-minmatch-own");
            compo.AddDropDown(new[] { "inherit", "own" }, new[] { globalLabel, own }, rw.MinMatchInherit ? 0 : 1, (value, on) =>
            {
                bool inherit = value == "inherit";
                if (inherit == rw.MinMatchInherit) return;
                rw.MinMatchInherit = inherit;
                Gui.LogAction(true, Lang.Get("canevents:admin-economy-row-minmatch") + " = " + (inherit ? globalLabel : own));
                Rebuild();
            }, ElementBounds.Fixed(controlX, cy, controlWidth, InputHeight), "eco-mm-dd");
            cy += SettingPitch;

            if (rw.MinMatchInherit)
            {
                AddFitText(compo, inner.fixedX + 10, cy + 3, inner.fixedWidth - 10,
                    Lang.Get("canevents:admin-economy-minmatch-follows"), AdminColors.Muted, "eco-mm-follows");
            }
            else
            {
                SettingLabel(compo, inner, cy, "eco-mms", Lang.Get("canevents:admin-economy-row-minmatch-seconds"),
                    Lang.Get("canevents:admin-economy-tip-minmatch-seconds"), true);
                NumberField.AddInt(ctx, ElementBounds.Fixed(controlX, cy, 80, InputHeight), "eco-minmatch",
                    () => rw.MinMatch, v => rw.MinMatch = v, 0);
            }
            cy += SettingPitch;

            // "Exempt" has no global value, inherit means off.
            SettingLabel(compo, inner, cy, "eco-rce", Lang.Get("canevents:admin-economy-row-ratecap-exempt"),
                Lang.Get("canevents:admin-economy-tip-ratecap-exempt"), rw.RateCapExempt != 0);
            TriDropDown(ctx, ElementBounds.Fixed(controlX, cy, controlWidth, InputHeight), "eco-rce-dd",
                Lang.Get("canevents:admin-economy-row-ratecap-exempt"), rw.RateCapExempt, false,
                v => rw.RateCapExempt = v);
            cy += SettingPitch;

            SettingLabel(compo, inner, cy, "eco-sc", Lang.Get("canevents:admin-economy-row-own-budget"),
                Lang.Get("canevents:admin-economy-tip-own-budget"), rw.SeparateCap != 0);
            TriDropDown(ctx, ElementBounds.Fixed(controlX, cy, controlWidth, InputHeight), "eco-sc-dd",
                Lang.Get("canevents:admin-economy-row-own-budget"), rw.SeparateCap, limits?.RateCapPerEvent,
                v => rw.SeparateCap = v);

            return y + height + Card.Gap;
        }

        // Warning colour while overriding the global value.
        private static void SettingLabel(GuiComposer compo, ElementBounds inner, double y, string key, string text,
                                         string tooltip, bool overriding)
        {
            var b = ElementBounds.Fixed(inner.fixedX, y + 5, SettingLabelWidth, LineHeight);
            var font = CairoFont.WhiteSmallText().WithColor(overriding ? AdminColors.Warning : AdminColors.Label);
            compo.AddStaticText(Card.Fit(text, font, SettingLabelWidth), font, b, key + "-label");
            string tip = overriding
                ? text + "\n" + tooltip + "\n\n" + Lang.Get("canevents:admin-economy-override-tip")
                : text + "\n" + tooltip;
            Tooltip.Add(compo, tip, b, key + "-label-tip");
        }

        // 0 = inherit, 1 = on, 2 = off
        private void TriDropDown(PageBuildContext ctx, ElementBounds bounds, string key, string label, int value,
                                 bool? globalValue, Action<int> set)
        {
            string inherit = globalValue == null
                ? Lang.Get("canevents:admin-economy-tri-inherit")
                : Lang.Get("canevents:admin-economy-tri-inherit-value",
                    Lang.Get(globalValue.Value ? "canevents:admin-on" : "canevents:admin-off"));
            string[] names = { inherit, Lang.Get("canevents:admin-economy-tri-on"), Lang.Get("canevents:admin-economy-tri-off") };
            string[] values = { "0", "1", "2" };
            int current = Math.Clamp(value, 0, 2);

            ctx.Compo.AddDropDown(values, names, current, (picked, on) =>
            {
                if (!int.TryParse(picked, out int v) || v == current) return;
                set(v);
                Gui.LogAction(true, label + " = " + names[Math.Clamp(v, 0, 2)]);
                Rebuild();
            }, bounds, key);
        }

        private void BuildDefaultsCard(PageBuildContext ctx, ElementBounds column, RewardLimitsMsg limits, double y)
        {
            var compo = ctx.Compo;
            double height = Math.Max(120, ctx.Bottom - y);
            var inner = Card.Frame(compo, column, y, height, Lang.Get("canevents:admin-economy-defaults-title"));

            string edit = Lang.Get("canevents:admin-economy-defaults-edit");
            double editWidth = CairoFont.SmallButtonText(EnumButtonStyle.Small).GetTextExtents(edit).Width + 20;
            var editBounds = ElementBounds.Fixed(column.fixedX + column.fixedWidth - Card.Padding - editWidth,
                y + Card.Padding, editWidth, 20);
            compo.AddSmallButton(edit, new ActionConsumable(() =>
            {
                SubTab = SubLimits;
                OnSubTabChanged(SubLimits);
                Rebuild();
                return true;
            }), editBounds, EnumButtonStyle.Small, "eco-def-edit");
            Tooltip.Add(compo, Lang.Get("canevents:admin-economy-defaults-edit-hint"), editBounds, "eco-def-edit-tip");

            double cy = inner.fixedY;
            AddFitText(compo, inner.fixedX, cy, inner.fixedWidth, Lang.Get("canevents:admin-economy-defaults-warn"),
                AdminColors.Warning, "eco-def-warn");
            cy += LineHeight + 2;

            if (limits == null)
            {
                AddText(compo, inner.fixedX, cy, inner.fixedWidth, Lang.Get("canevents:admin-economy-loading-limits"),
                    AdminColors.Muted, "eco-def-loading");
                return;
            }

            DefaultsTable(ctx, ElementBounds.Fixed(inner.fixedX, cy, inner.fixedWidth, 0), "eco-def",
                limits.WinnerItemCode, limits.WinnerQuantity, limits.ParticipationItemCode, limits.ParticipationQuantity,
                limits.Participation);
        }

        // Icons double as validation, a bad code shows up here instead of at payout.
        private void DefaultsTable(PageBuildContext ctx, ElementBounds anchor, string key,
                                   string winnerCode, int winnerQty, string partCode, int partQty, bool participationOn)
        {
            var spec = new ColumnSpec(
                new ColumnDef(null, 32),
                new ColumnDef(Lang.Get("canevents:admin-economy-col-default"), 0),
                new ColumnDef(Lang.Get("canevents:admin-economy-col-qty"), 44, EnumTextOrientation.Right));

            string partLabel = Lang.Get("canevents:admin-economy-def-participation") + " ("
                               + Lang.Get(participationOn ? "canevents:admin-on" : "canevents:admin-off") + ")";
            var rows = new List<ColumnRow>
            {
                DefaultRow(Lang.Get("canevents:admin-economy-def-winner"), winnerCode, winnerQty),
                DefaultRow(partLabel, partCode, partQty)
            };
            rows[1].WithCellTooltip(1, Lang.Get("canevents:admin-economy-def-participation-tip") + "\n" + StatusOf(partCode, partQty));
            ColumnTable.Add(ctx, anchor, spec, rows, 2 * 34 + 10, new ScrollableListOptions { Key = key + "-list" });
        }

        private ColumnRow DefaultRow(string label, string code, int qty)
        {
            var row = new ColumnRow(null, label, NumberParse.Format(qty));
            bool none = string.IsNullOrWhiteSpace(code);
            if (!none)
            {
                row.IconCodes = new List<string> { code };
                row.IconColumn = 0;
            }
            double[] color = none ? AdminColors.Muted
                : GuiItemUtil.Resolve(Capi, code) == null ? AdminColors.Danger
                : qty <= 0 ? AdminColors.Warning
                : AdminColors.Value;
            row.WithColor(1, color);
            row.WithColor(2, qty <= 0 ? AdminColors.Warning : AdminColors.Value);
            row.WithCellTooltip(1, StatusOf(code, qty));
            return row;
        }

        private string StatusOf(string code, int qty)
        {
            if (string.IsNullOrWhiteSpace(code)) return Lang.Get("canevents:admin-economy-def-none");
            if (GuiItemUtil.Resolve(Capi, code) == null) return code + " - " + Lang.Get("canevents:admin-economy-def-unknown");
            if (qty <= 0) return code + " - " + Lang.Get("canevents:admin-economy-def-qty0");
            return code;
        }

        private static void AddText(GuiComposer compo, double x, double y, double width, string text, double[] color, string key)
        {
            compo.AddStaticText(text, CairoFont.WhiteSmallText().WithColor(color),
                ElementBounds.Fixed(x, y, width, LineHeight), key);
        }

        private static void AddFitText(GuiComposer compo, double x, double y, double width, string text, double[] color, string key)
        {
            var font = CairoFont.WhiteSmallText().WithColor(color);
            var b = ElementBounds.Fixed(x, y, width, LineHeight);
            string shown = Card.Fit(text, font, width);
            compo.AddStaticText(shown, font, b, key);
            if (shown != text) Tooltip.Add(compo, text, b, key + "-tip");
        }
    }
}
