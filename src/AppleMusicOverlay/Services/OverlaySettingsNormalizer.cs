using AppleMusicOverlay.Models;

namespace AppleMusicOverlay.Services;

public static class OverlaySettingsNormalizer
{
    public static OverlaySettings Normalize(OverlaySettings settings)
    {
        settings.LeftPercent = Clamp(settings.LeftPercent, 0, 1);
        settings.TopPercent = Clamp(settings.TopPercent, 0, 1);
        settings.ScalePercent = Clamp(settings.ScalePercent, 70, 180);
        settings.DisplaySeconds = (int)Clamp(settings.DisplaySeconds, 1, 10);
        settings.CoverShadowSizePercent = Clamp(settings.CoverShadowSizePercent, 0, 100);

        if (!Enum.IsDefined(settings.DisplayStyle))
        {
            settings.DisplayStyle = DisplayStyle.MinimalCover;
        }

        settings.KeyboardPrevious = NormalizeOptionalText(settings.KeyboardPrevious);
        settings.KeyboardNext = NormalizeOptionalText(settings.KeyboardNext);
        settings.KeyboardToggle = NormalizeOptionalText(settings.KeyboardToggle);
        settings.KeyboardTestOverlay = NormalizeOptionalText(settings.KeyboardTestOverlay);
        settings.CaptureSourceAppUserModelId = settings.CaptureSourceAppUserModelId?.Trim() ?? string.Empty;
        return settings;
    }

    private static double Clamp(double value, double min, double max)
    {
        if (value < min)
        {
            return min;
        }

        if (value > max)
        {
            return max;
        }

        return value;
    }

    private static string NormalizeText(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static string NormalizeOptionalText(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }
}
