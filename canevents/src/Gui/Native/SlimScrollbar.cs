using System;
using Cairo;
using Vintagestory.API.Client;

namespace canevents.src.Gui.Native
{
    // Vanilla scrollbar bakes its trough into the static background; this one draws only the handle,
    // so it can be hidden per frame. Base class already ignores input while the handle fills the bar.
    public class SlimScrollbar : GuiElementScrollbar
    {
        public bool Visible = true;

        public SlimScrollbar(ICoreClientAPI capi, Action<float> onNewScrollbarValue, ElementBounds bounds)
            : base(capi, onNewScrollbarValue, bounds)
        {
        }

        public override void ComposeElements(Context ctxStatic, ImageSurface surface)
        {
            Bounds.CalcWorldBounds();
            RecomposeHandle();
        }

        public override void RenderInteractiveElements(float deltaTime)
        {
            if (!Visible) return;
            base.RenderInteractiveElements(deltaTime);
        }
    }
}
