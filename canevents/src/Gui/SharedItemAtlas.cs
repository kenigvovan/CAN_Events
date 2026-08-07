using Vintagestory.API.Client;

namespace canevents.src.Gui
{
    /// <summary>
    /// One icon atlas shared by every dialog that draws items, instead of a framebuffer and cache
    /// each. Ref-counted rather than owned by one dialog, so closing one does not leave the other
    /// drawing into a disposed framebuffer. Main-thread only, so no locking.
    /// </summary>
    public static class SharedItemAtlas
    {
        private const int AtlasSize = 1024;
        private const int IconSize = 48;

        private static ItemIconAtlas instance;
        private static int refCount;

        public static ItemIconAtlas Acquire(ICoreClientAPI capi)
        {
            if (instance == null) instance = new ItemIconAtlas(capi, AtlasSize, IconSize);
            refCount++;
            return instance;
        }

        public static void Release()
        {
            if (instance == null) return;
            refCount--;
            if (refCount > 0) return;

            instance.Dispose();
            instance = null;
            refCount = 0;
        }
    }
}
