using System;
using System.Collections.Generic;
using Vintagestory.API.Client;

namespace canevents.src.Gui.Admin.Pages
{
    // Per-rebuild state. Pages are singletons, so build-scoped data lives here, not in fields.
    public sealed class PageBuildContext
    {
        public AdminPanelGui Gui;

        // Same object as Gui.State.
        public AdminGuiState State;

        // Pages add elements but never call Compose.
        public GuiComposer Compo;

        // Zero-height row at the top of the content area; walk down with BelowCopy.
        public ElementBounds Current;

        public ElementBounds Line;

        // Between the tab row and the status line.
        public ElementBounds Main;

        public double Width => Main.fixedWidth;

        // Same coordinate space as Current.
        public double Bottom => Main.fixedY + Main.fixedHeight;

        private readonly List<Action> after = new List<Action>();

        // For things that need final laid-out sizes, e.g. scrollbar SetHeights.
        public void AfterCompose(Action action) => after.Add(action);

        // Runs outside the build guard, so each step is isolated.
        internal void RunAfterCompose()
        {
            var actions = after.ToArray();
            after.Clear();
            foreach (var action in actions)
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    Gui?.Capi?.Logger.Error("[canevents] Admin panel: a post-compose step failed: {0}", e);
                }
            }
        }

        public ElementBounds Column(double x, double width)
            => ElementBounds.Fixed(Main.fixedX + x, Main.fixedY, width, 0);
    }
}
