using System;
using Vintagestory.API.Config;

namespace canevents.src.Gui.Admin.Widgets
{
    public enum SettingKind
    {
        Group, Flag, Int, Double, Text,
        // Fixed set of values, picked in a side panel.
        Choice
    }

    public sealed class SettingOption
    {
        public string Value;
        public string Label;

        public SettingOption(string value, string label)
        {
            Value = value;
            Label = label;
        }
    }

    // Row of a SettingsTable. Holds no value itself, only getters/setters into page state.
    public sealed class SettingRow
    {
        public SettingKind Kind;

        // Unique within the table, the edit line tracks its row by it.
        public string Id;

        // Already translated.
        public string Label;

        // null shows the generic "click to edit" hint.
        public string Tooltip;

        public Func<bool> GetFlag;
        public Action<bool> SetFlag;

        public Func<int> GetInt;
        public Action<int> SetInt;
        public int MinInt = int.MinValue, MaxInt = int.MaxValue;

        public Func<double> GetDouble;
        public Action<double> SetDouble;
        public double Min = double.MinValue, Max = double.MaxValue;

        public Func<string> GetText;
        public Action<string> SetText;

        // Shown in the list instead of the raw value, e.g. a choice's label.
        public Func<string> GetDisplay;

        public SettingOption[] Options;

        public static SettingRow Group(string label)
            => new SettingRow { Kind = SettingKind.Group, Label = label };

        public static SettingRow Flag(string id, string label, Func<bool> get, Action<bool> set, string tooltip = null)
            => new SettingRow { Kind = SettingKind.Flag, Id = id, Label = label, GetFlag = get, SetFlag = set, Tooltip = tooltip };

        public static SettingRow Int(string id, string label, Func<int> get, Action<int> set,
                                     int min = int.MinValue, int max = int.MaxValue, string tooltip = null)
            => new SettingRow { Kind = SettingKind.Int, Id = id, Label = label, GetInt = get, SetInt = set, MinInt = min, MaxInt = max, Tooltip = tooltip };

        public static SettingRow Dbl(string id, string label, Func<double> get, Action<double> set,
                                     double min = double.MinValue, double max = double.MaxValue, string tooltip = null)
            => new SettingRow { Kind = SettingKind.Double, Id = id, Label = label, GetDouble = get, SetDouble = set, Min = min, Max = max, Tooltip = tooltip };

        public static SettingRow Text(string id, string label, Func<string> get, Action<string> set,
                                      Func<string> display = null, string tooltip = null)
            => new SettingRow { Kind = SettingKind.Text, Id = id, Label = label, GetText = get, SetText = set, GetDisplay = display, Tooltip = tooltip };

        public static SettingRow Choice(string id, string label, Func<string> get, Action<string> set,
                                        params SettingOption[] options)
        {
            var row = new SettingRow { Kind = SettingKind.Choice, Id = id, Label = label, GetText = get, SetText = set, Options = options };
            row.GetDisplay = () =>
            {
                string v = get();
                foreach (var o in options)
                    if (string.Equals(o.Value, v, StringComparison.OrdinalIgnoreCase)) return o.Label;
                return v ?? "";
            };
            return row;
        }

        public string CurrentValue()
        {
            switch (Kind)
            {
                case SettingKind.Flag: return GetFlag() ? Lang.Get("canevents:admin-on") : Lang.Get("canevents:admin-off");
                case SettingKind.Int: return NumberParse.Format(GetInt());
                case SettingKind.Double: return NumberParse.Format(GetDouble());
                case SettingKind.Text:
                case SettingKind.Choice:
                    return GetText() ?? "";
                default: return "";
            }
        }

        public string DisplayValue() => GetDisplay != null ? GetDisplay() : CurrentValue();

        public bool TryApply(string typed, out string errorLangKey)
        {
            errorLangKey = null;
            switch (Kind)
            {
                case SettingKind.Int:
                    if (!NumberParse.TryInt(typed, out int i)) { errorLangKey = "canevents:admin-input-not-a-number"; return false; }
                    if (i < MinInt || i > MaxInt) { errorLangKey = "canevents:admin-input-out-of-range"; return false; }
                    SetInt(i);
                    return true;
                case SettingKind.Double:
                    if (!NumberParse.TryDouble(typed, out double d)) { errorLangKey = "canevents:admin-input-not-a-number"; return false; }
                    if (d < Min || d > Max) { errorLangKey = "canevents:admin-input-out-of-range"; return false; }
                    SetDouble(d);
                    return true;
                case SettingKind.Text:
                    SetText(typed ?? "");
                    return true;
                case SettingKind.Flag:
                    SetFlag(!GetFlag());
                    return true;
                case SettingKind.Choice:
                    foreach (var o in Options)
                    {
                        if (string.Equals(o.Value, typed?.Trim(), StringComparison.OrdinalIgnoreCase))
                        {
                            SetText(o.Value);
                            return true;
                        }
                    }
                    errorLangKey = "canevents:admin-input-not-an-option";
                    return false;
                default:
                    return false;
            }
        }

        public string RangeText()
        {
            if (Kind == SettingKind.Int && (MinInt != int.MinValue || MaxInt != int.MaxValue))
                return Lang.Get("canevents:admin-field-range",
                    MinInt == int.MinValue ? "-" : NumberParse.Format(MinInt),
                    MaxInt == int.MaxValue ? "-" : NumberParse.Format(MaxInt));
            if (Kind == SettingKind.Double && (Min != double.MinValue || Max != double.MaxValue))
                return Lang.Get("canevents:admin-field-range",
                    Min == double.MinValue ? "-" : NumberParse.Format(Min),
                    Max == double.MaxValue ? "-" : NumberParse.Format(Max));
            return null;
        }
    }
}
