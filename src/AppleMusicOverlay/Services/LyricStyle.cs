using System.Globalization;
using System.Windows.Media;

namespace AppleMusicOverlay.Services;

/// <summary>
/// Shared helpers for the floating lyric styling settings (color parsing / formatting).
/// Kept in one place so the settings window and the lyric window agree on the same rules.
/// </summary>
public static class LyricStyle
{
    public const string DefaultColorHex = "#FFFFFF";
    public const double MinOpacity = 0.05;
    public const double MaxOpacity = 1.0;

    /// <summary>Parses "#RRGGBB", "RRGGBB", "#AARRGGBB" or "#RGB". Falls back to white.</summary>
    public static Color ParseColor(string? value) =>
        TryParseColor(value, out Color color) ? color : Colors.White;

    /// <summary>Returns false for partial input such as "#FF" or "abc".</summary>
    public static bool TryParseColor(string? value, out Color color)
    {
        color = Colors.White;
        if (string.IsNullOrWhiteSpace(value)) return false;
        string text = value.Trim();
        if (!text.StartsWith('#')) text = "#" + text;
        if (text.Length == 4)
        {
            // Expand "#RGB" to "#RRGGBB".
            text = $"#{text[1]}{text[1]}{text[2]}{text[2]}{text[3]}{text[3]}";
        }
        if (text.Length is not (7 or 9)) return false;
        try
        {
            if (ColorConverter.ConvertFromString(text) is Color parsed)
            {
                color = parsed;
                return true;
            }
        }
        catch (FormatException) { }
        catch (NotSupportedException) { }
        catch (ArgumentException) { }
        return false;
    }

    public static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    public static string Normalize(string? value) => ToHex(ParseColor(value));

    public static Brush CreateBrush(string? value)
    {
        var brush = new SolidColorBrush(ParseColor(value));
        brush.Freeze();
        return brush;
    }

    public static double ClampOpacity(double value) =>
        double.IsNaN(value) ? MaxOpacity : Math.Clamp(value, MinOpacity, MaxOpacity);

    /// <summary>Parses the "透明度" text box. Accepts "80", "80%" or "0.8" (0.05-1 range).</summary>
    public static double ParseOpacityPercent(string? text, double fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return ClampOpacity(fallback);
        string cleaned = text.Trim().TrimEnd('%').Trim();
        if (!double.TryParse(cleaned, NumberStyles.Float, CultureInfo.CurrentCulture, out double value) &&
            !double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return ClampOpacity(fallback);
        }
        if (value > 0 && value <= MaxOpacity && (cleaned.Contains('.') || cleaned.Contains(','))) return ClampOpacity(value);
        return ClampOpacity(value / 100d);
    }
}
