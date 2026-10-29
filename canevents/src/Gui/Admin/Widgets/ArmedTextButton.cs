using System;
using canevents.src.Gui.Admin.GuiElements;
using canevents.src.Gui.Admin.Pages;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace canevents.src.Gui.Admin.Widgets
{
    // One armed at a time (AdminGuiState.ArmedKey); the panel disarms on timeout and tab switch.
    public static class ArmedTextButton
    {
        public const long ArmWindowMs = 4000;

        // Fits both captions so the button doesn't resize when armed.
        public static double Width(string caption, string armedCaption = null)
            => Math.Max(ButtonRow.ButtonWidth(caption), ButtonRow.ButtonWidth(armedCaption ?? Lang.Get("canevents:admin-armed-confirm")));

        public static string WiderCaption(string caption, string armedCaption)
            => ButtonRow.ButtonWidth(caption) >= ButtonRow.ButtonWidth(armedCaption) ? caption : armedCaption;

        public static ElementBounds Add(PageBuildContext ctx, ElementBounds bounds, string key, string caption,
                                        Action onConfirmed, string armedCaption = null, string tooltip = null)
        {
            var state = ctx.State;
            var gui = ctx.Gui;
            bool armed = IsArmed(state, gui, key);

            string shown = armed ? (armedCaption ?? Lang.Get("canevents:admin-armed-confirm")) : caption;

            ctx.Compo.AddButton(shown, new ActionConsumable(() =>
            {
                if (IsArmed(state, gui, key))
                {
                    state.ArmedKey = null;
                    onConfirmed();
                }
                else
                {
                    state.ArmedKey = key;
                    state.ArmedAtMs = gui.NowMs;
                }
                gui.RequestRebuild();
                return true;
            }), bounds, EnumButtonStyle.Normal, key);

            string tip = armed ? Lang.Get("canevents:admin-armed-hint") : tooltip;
            if (tip != null) Tooltip.Add(ctx.Compo, tip, bounds, "tip-" + key);
            return bounds;
        }

        // Table rows can't hold composer buttons; shares the same ArmedKey slot.
        public static ColumnRow AddToRow(PageBuildContext ctx, ColumnRow row, int column, string key, string caption,
                                         string armedCaption, string tooltip, Action onConfirmed)
        {
            var state = ctx.State;
            var gui = ctx.Gui;
            bool armed = IsArmed(state, gui, key);
            return row.WithButton(column, armed ? (armedCaption ?? Lang.Get("canevents:admin-armed-confirm")) : caption, () =>
            {
                if (IsArmed(state, gui, key))
                {
                    state.ArmedKey = null;
                    onConfirmed();
                }
                else
                {
                    state.ArmedKey = key;
                    state.ArmedAtMs = gui.NowMs;
                }
                gui.RequestRebuild();
            }, armed ? Lang.Get("canevents:admin-armed-hint") : tooltip);
        }

        public static bool IsArmed(AdminGuiState state, AdminPanelGui gui, string key)
            => state.ArmedKey == key && gui.NowMs - state.ArmedAtMs <= ArmWindowMs;

        // Called every frame; true means the caller should rebuild.
        public static bool ExpireIfStale(AdminGuiState state, long nowMs)
        {
            if (state.ArmedKey == null || nowMs - state.ArmedAtMs <= ArmWindowMs) return false;
            state.ArmedKey = null;
            return true;
        }
    }
}
