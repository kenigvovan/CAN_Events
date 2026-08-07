using System;
using Cairo;
using Vintagestory.API.Client;

namespace canevents.src.Gui.Native
{
    /// <summary>
    /// A scrollbar that can disappear. <see cref="GuiElementScrollbar"/> bakes its trough into the
    /// dialog's static background, so a list that fits still shows an empty groove until the whole
    /// dialog is recomposed. This draws no trough - only the handle, a render-pass texture that can
    /// be hidden per frame. Input needs no guarding: the base class already ignores clicks and the
    /// wheel while the handle fills the bar, which is exactly when <see cref="Visible"/> is false.
    /// </summary>
    public class SlimScrollbar : GuiElementScrollbar
    {
        /// <summary>Set by the owner once it knows whether the content overflows.</summary>
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
