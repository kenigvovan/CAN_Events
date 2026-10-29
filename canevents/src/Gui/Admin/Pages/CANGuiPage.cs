using System;
using System.Collections.Generic;
using canevents.src.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace canevents.src.Gui.Admin.Pages
{
    public sealed class PageWatchList
    {
        internal readonly List<(Action<Action> subscribe, Action<Action> unsubscribe)> Entries
            = new List<(Action<Action>, Action<Action>)>();

        public PageWatchList Add<T>(ServerState<T> state) where T : class
        {
            Entries.Add((h => state.Changed += h, h => state.Changed -= h));
            return this;
        }
    }

    // Pages are singletons: don't hold composer elements/bounds between builds, the composer drops them on rebuild.
    public abstract class CANGuiPage
    {
        protected AdminPanelGui Gui { get; private set; }
        protected AdminGuiState State => Gui.State;
        protected ClientNetworkHandler Net => Gui.Net;
        protected ICoreClientAPI Capi => Gui.Capi;
        protected PlaceHighlighter Highlighter => Gui.Highlighter;

        internal void Attach(AdminPanelGui gui) => Gui = gui;

        internal void Build(AdminPanelGui gui, PageBuildContext ctx)
        {
            Gui = gui;
            if (!IsAvailable(out string reasonLangKey))
            {
                BuildUnavailable(ctx, reasonLangKey);
                return;
            }
            BuildContent(ctx);
        }

        protected abstract void BuildContent(PageBuildContext ctx);

        public virtual void Watch(PageWatchList watch) { }

        // Runs after the tab's standard data request.
        public virtual void OnShown() { }

        public virtual void OnHidden() { }

        // HG loot spot marker, polled every frame.
        public virtual bool WantsPlacementMarker(out int tierIdx)
        {
            tierIdx = -1;
            return false;
        }

        protected virtual bool IsAvailable(out string reasonLangKey)
        {
            reasonLangKey = null;
            return true;
        }

        protected virtual void BuildUnavailable(PageBuildContext ctx, string reasonLangKey)
        {
            if (reasonLangKey == null) return;

            var bounds = ctx.Current.FlatCopy().WithFixedHeight(30);
            bounds.fixedY += 40;
            ctx.Compo.AddStaticText(Lang.Get(reasonLangKey),
                CairoFont.WhiteMediumText().WithOrientation(EnumTextOrientation.Center),
                bounds, "page-unavailable");
        }

        protected void Rebuild() => Gui.RequestRebuild();

        protected void GoTo(PanelTab tab) => Gui.SelectTab(tab);

        protected void Act(string log, Action send) => Gui.Act(log, send);
    }
}
