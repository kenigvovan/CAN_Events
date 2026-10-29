using System.Globalization;

namespace canevents.src.Gui.Admin.Widgets
{
    public enum InputKind
    {
        Text, Integer, Decimal
    }

    // Accepts both ',' and '.' as decimal separator.
    public static class NumberParse
    {
        public static bool TryInt(string typed, out int value)
            => int.TryParse(typed?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

        public static bool TryDouble(string typed, out double value)
            => double.TryParse(typed?.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

        public static bool IsAccepted(string typed, InputKind kind)
        {
            switch (kind)
            {
                case InputKind.Integer: return TryInt(typed, out _);
                case InputKind.Decimal: return TryDouble(typed, out _);
                default: return true;
            }
        }

        public static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

        public static string Format(int value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
