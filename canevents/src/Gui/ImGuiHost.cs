using System;
using Vintagestory.API.Client;
using VSImGui;
using VSImGui.API;

namespace canevents.src.Gui
{
    /// <summary>
    /// Attach/detach half of a VSImGui client panel: hook the draw callback on world load, mirror
    /// the shared dialog's Closed event, unhook on Dispose. Both publishers outlive the panel on a
    /// hot-reload, so handlers are held in fields to stay detachable - a surviving LevelFinalize
    /// handler would re-attach a dead panel's Draw on the next world load.
    /// </summary>
    public sealed class ImGuiHost
    {
        private readonly ICoreClientAPI capi;
        private readonly ImGuiModSystem imguiSys;
        // The mod system's own delegate type, not a Func: += onto its Draw event needs the
        // declared type, and a Func<float, CallbackGUIStatus> does not implicitly convert.
        private readonly DrawCallbackDelegate draw;
        private readonly Action onLevelReady;
        private readonly Action onClosed;
        private Action _onLevelFinalize;
        private Action _onImguiClosed;

        /// <param name="draw">The panel's per-frame draw callback.</param>
        /// <param name="onLevelReady">Ran just before the draw hook is attached, for resources that
        /// need a loaded world (the shared item atlas).</param>
        /// <param name="onClosed">Ran when the shared ImGui dialog closes (Esc / toggle), for panels
        /// that keep their own open flag.</param>
        public ImGuiHost(ICoreClientAPI capi, DrawCallbackDelegate draw,
                         Action onLevelReady = null, Action onClosed = null)
        {
            this.capi = capi;
            this.draw = draw;
            this.onLevelReady = onLevelReady;
            this.onClosed = onClosed;
            imguiSys = capi.ModLoader.GetModSystem<ImGuiModSystem>();
            if (imguiSys == null) return;

            _onLevelFinalize = () =>
            {
                this.onLevelReady?.Invoke();
                imguiSys.Draw += this.draw;
            };
            capi.Event.LevelFinalize += _onLevelFinalize;

            if (this.onClosed != null)
            {
                _onImguiClosed = () => this.onClosed();
                imguiSys.Closed += _onImguiClosed;
            }
        }

        /// <summary>Whether VSImGui is present at all. Without it the panel simply never draws.</summary>
        public bool Available => imguiSys != null;

        /// <summary>Brings the shared ImGui window up, if VSImGui is loaded.</summary>
        public void Show() => imguiSys?.Show();

        public void Dispose()
        {
            if (imguiSys != null)
            {
                imguiSys.Draw -= draw;
                if (_onImguiClosed != null) imguiSys.Closed -= _onImguiClosed;
            }
            if (_onLevelFinalize != null) capi.Event.LevelFinalize -= _onLevelFinalize;
            _onLevelFinalize = null;
            _onImguiClosed = null;
        }
    }
}
