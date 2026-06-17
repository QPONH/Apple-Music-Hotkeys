using AppleMusicOverlay.Models;
using AppleMusicOverlay.Services;

namespace AppleMusicOverlay.Tests;

public sealed class OverlaySettingsTests
{
    [Fact]
    public void DefaultSettingsUseMinimalCoverStyle()
    {
        var settings = new OverlaySettings();

        Assert.Equal(DisplayStyle.MinimalCover, settings.DisplayStyle);
        Assert.True(settings.ShowTitle);
        Assert.True(settings.ShowArtist);
        Assert.False(settings.ShowControls);
        Assert.Equal(5, settings.DisplaySeconds);
        Assert.Equal(80d, settings.CoverShadowSizePercent);
        Assert.Equal(string.Empty, settings.CaptureSourceAppUserModelId);
    }

    [Fact]
    public void NormalizeClampsNumericSettings()
    {
        var settings = new OverlaySettings
        {
            LeftPercent = -0.25,
            TopPercent = 2.0,
            ScalePercent = 240,
            DisplaySeconds = 45,
            CoverShadowSizePercent = 180.5
        };

        OverlaySettings normalized = OverlaySettingsNormalizer.Normalize(settings);

        Assert.Equal(0, normalized.LeftPercent);
        Assert.Equal(1, normalized.TopPercent);
        Assert.Equal(180, normalized.ScalePercent);
        Assert.Equal(10, normalized.DisplaySeconds);
        Assert.Equal(100d, normalized.CoverShadowSizePercent);
    }

    [Fact]
    public void NormalizePreservesFractionalCoverShadowSize()
    {
        var settings = new OverlaySettings
        {
            CoverShadowSizePercent = 0.5
        };

        OverlaySettings normalized = OverlaySettingsNormalizer.Normalize(settings);

        Assert.Equal(0.5, normalized.CoverShadowSizePercent);
    }

    [Fact]
    public void NormalizePreservesFractionalScalePercent()
    {
        var settings = new OverlaySettings
        {
            ScalePercent = 110.5
        };

        OverlaySettings normalized = OverlaySettingsNormalizer.Normalize(settings);

        Assert.Equal(110.5, normalized.ScalePercent);
    }

    [Fact]
    public void NormalizeAllowsEmptyHotkeysForDeletedBindings()
    {
        var settings = new OverlaySettings
        {
            KeyboardPrevious = " ",
            KeyboardNext = string.Empty,
            KeyboardToggle = "Ctrl+Shift+Down",
            KeyboardTestOverlay = " Ctrl+Shift+Up "
        };

        OverlaySettings normalized = OverlaySettingsNormalizer.Normalize(settings);

        Assert.Equal(string.Empty, normalized.KeyboardPrevious);
        Assert.Equal(string.Empty, normalized.KeyboardNext);
        Assert.Equal("Ctrl+Shift+Down", normalized.KeyboardToggle);
        Assert.Equal("Ctrl+Shift+Up", normalized.KeyboardTestOverlay);
    }

    [Fact]
    public void SettingsServiceRoundTripsJson()
    {
        string dir = Path.Combine(Path.GetTempPath(), "AppleMusicOverlay.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "settings.json");
        var service = new OverlaySettingsService(path);
        var input = new OverlaySettings
        {
            LeftPercent = 0.4,
            TopPercent = 0.7,
            KeyboardNext = "Ctrl+Shift+Right",
            PauseOverlay = true
        };

        service.Save(input);
        OverlaySettings loaded = service.Load();

        Assert.Equal(0.4, loaded.LeftPercent);
        Assert.Equal(0.7, loaded.TopPercent);
        Assert.Equal("Ctrl+Shift+Right", loaded.KeyboardNext);
        Assert.True(loaded.PauseOverlay);
    }
}
