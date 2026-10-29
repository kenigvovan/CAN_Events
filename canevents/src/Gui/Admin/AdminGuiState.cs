using System.Collections.Generic;

namespace canevents.src.Gui.Admin
{
    // Generic widget state keyed by element key. The composer drops all elements on every rebuild.
    public sealed class AdminGuiState
    {
        public PanelTab Tab = PanelTab.Dashboard;

        public readonly Dictionary<PanelTab, int> SubTabs = new Dictionary<PanelTab, int>();

        // Missing key = section default.
        public readonly Dictionary<string, bool> Sections = new Dictionary<string, bool>();

        public readonly Dictionary<string, float> Scroll = new Dictionary<string, float>();

        // Unparsable field text ("-", "1.") kept across rebuilds.
        public readonly Dictionary<string, string> Drafts = new Dictionary<string, string>();

        // ArmedAtMs is client ms.
        public string ArmedKey;
        public long ArmedAtMs;

        public readonly Dictionary<string, string> SettingsEditKey = new Dictionary<string, string>();
        public readonly Dictionary<string, string> SettingsEditValue = new Dictionary<string, string>();
        public readonly Dictionary<string, string> SettingsFilter = new Dictionary<string, string>();

        public bool IsSectionOpen(string key, bool defaultOpen)
            => Sections.TryGetValue(key, out bool open) ? open : defaultOpen;

        public float ScrollOf(string key) => Scroll.TryGetValue(key, out float v) ? v : 0f;
    }
}
