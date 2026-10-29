using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace canevents.src.Gui.Admin.GuiElements
{
    // Same approach as Native.EventTileRow. Per frame: BeginFrame, Render per icon, EndFrame.
    public sealed class ItemIconRenderer : IDisposable
    {
        // Vanilla slot ratio (48px slot, 25.6 item). Size scales the model, not a box, so long meshes overflow.
        public const double UnscaledIconSize = 30;
        public const double UnscaledIconItemSize = 17;

        private readonly ICoreClientAPI capi;

        // Reused per icon. Unscaled because CalcWorldBounds applies GUIScale again.
        private readonly ElementBounds scissorBounds = ElementBounds.Fixed(0, 0, 0, 0).WithEmptyParent();

        // Lazy. ItemstackComponentBase lifts the scissor, so the list clip doesn't cut the tooltip.
        private ItemstackComponentBase tooltipRenderer;
        private ItemSlot hoveredSlot;

        public ItemIconRenderer(ICoreClientAPI capi)
        {
            this.capi = capi;
        }

        public void BeginFrame() => hoveredSlot = null;

        // x, y, size in scaled screen pixels. mayHover = false for cells scrolled out of view.
        public void Render(ItemSlot slot, double x, double y, double size, float deltaTime, bool mayHover,
                           double itemSize = -1)
        {
            if (slot?.Itemstack == null) return;
            if (itemSize <= 0) itemSize = size * (UnscaledIconItemSize / UnscaledIconSize);

            // Stacking scissor, intersected with the list clip.
            capi.Render.PushScissor(Scissor(x, y, size), true);
            capi.Render.RenderItemstackToGui(slot, x + size / 2, y + size / 2, 100, (float)itemSize,
                ColorUtil.WhiteArgb, deltaTime, true, false, true);
            capi.Render.PopScissor();

            if (!mayHover) return;
            int mouseX = capi.Input.MouseX, mouseY = capi.Input.MouseY;
            if (mouseX >= x && mouseX < x + size && mouseY >= y && mouseY < y + size)
                hoveredSlot = slot;
        }

        public void EndFrame(float deltaTime)
        {
            if (hoveredSlot == null) return;
            tooltipRenderer ??= new ItemstackComponentBase(capi);
            tooltipRenderer.RenderItemstackTooltip(hoveredSlot, capi.Input.MouseX, capi.Input.MouseY, deltaTime);
        }

        private ElementBounds Scissor(double x, double y, double size)
        {
            double s = RuntimeEnv.GUIScale;
            scissorBounds.fixedX = x / s;
            scissorBounds.fixedY = y / s;
            scissorBounds.fixedWidth = size / s;
            scissorBounds.fixedHeight = size / s;
            scissorBounds.CalcWorldBounds();
            return scissorBounds;
        }

        // Items first, then blocks. Null if unknown (e.g. client lacks the mod).
        public static DummySlot SlotFor(ICoreClientAPI capi, string code, int quantity = 1)
        {
            if (capi == null || string.IsNullOrEmpty(code)) return null;
            var loc = new AssetLocation(code);
            Item item = capi.World.GetItem(loc);
            if (item != null) return new DummySlot(new ItemStack(item, Math.Max(1, quantity)));
            Block block = capi.World.GetBlock(loc);
            if (block != null) return new DummySlot(new ItemStack(block, Math.Max(1, quantity)));
            return null;
        }

        public void Dispose()
        {
            tooltipRenderer?.Dispose();
            tooltipRenderer = null;
        }
    }
}
