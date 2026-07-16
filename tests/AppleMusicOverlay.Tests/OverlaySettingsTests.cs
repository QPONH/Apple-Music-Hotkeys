using AppleMusicOverlay.Models;
using AppleMusicOverlay.Services;
using System.Globalization;

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
        Assert.False(settings.AutoHideOnMouseNear);
        Assert.True(settings.ShowOverlayOnTrackChange);
        Assert.Equal("default", settings.OverlayTrackFont);
        Assert.Equal(string.Empty, settings.CaptureSourceAppUserModelId);
        Assert.Equal("zh-CN", settings.LanguageCode);
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
            PauseOverlay = true,
            AutoHideOnMouseNear = true,
            ShowOverlayOnTrackChange = false,
            OverlayTrackFont = "sf-pro"
        };

        service.Save(input);
        OverlaySettings loaded = service.Load();

        Assert.Equal(0.4, loaded.LeftPercent);
        Assert.Equal(0.7, loaded.TopPercent);
        Assert.Equal("Ctrl+Shift+Right", loaded.KeyboardNext);
        Assert.True(loaded.PauseOverlay);
        Assert.True(loaded.AutoHideOnMouseNear);
        Assert.False(loaded.ShowOverlayOnTrackChange);
        Assert.Equal("sf-pro", loaded.OverlayTrackFont);
    }

    [Fact]
    public void SettingsServiceUsesAutomaticTrackChangeOverlayForLegacyJson()
    {
        string dir = Path.Combine(Path.GetTempPath(), "AppleMusicOverlay.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, "{\"PauseOverlay\":false}");

        var service = new OverlaySettingsService(path);
        OverlaySettings loaded = service.Load();

        Assert.True(loaded.ShowOverlayOnTrackChange);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bad-font")]
    public void NormalizeFallsBackToDefaultForUnknownOverlayTrackFont(string? overlayTrackFont)
    {
        var settings = new OverlaySettings
        {
            OverlayTrackFont = overlayTrackFont!
        };

        OverlaySettings normalized = OverlaySettingsNormalizer.Normalize(settings);

        Assert.Equal("default", normalized.OverlayTrackFont);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("fr-FR")]
    public void NormalizeFallsBackToSimplifiedChineseForUnknownLanguage(string? languageCode)
    {
        var settings = new OverlaySettings
        {
            LanguageCode = languageCode!
        };

        OverlaySettings normalized = OverlaySettingsNormalizer.Normalize(settings);

        Assert.Equal("zh-CN", normalized.LanguageCode);
    }

    [Fact]
    public void SettingsServicePersistsLanguageCode()
    {
        string dir = Path.Combine(Path.GetTempPath(), "AppleMusicOverlay.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "settings.json");
        var service = new OverlaySettingsService(path);

        service.Save(new OverlaySettings { LanguageCode = "en-US" });
        OverlaySettings loaded = service.Load();

        Assert.Equal("en-US", loaded.LanguageCode);
    }

    [Theory]
    [InlineData("en-US", "en-US")]
    [InlineData("fr-FR", "en-US")]
    [InlineData("zh-CN", "zh-CN")]
    [InlineData("zh-HK", "zh-CN")]
    public void SettingsServiceUsesSystemLanguageForFirstLaunch(string cultureName, string expectedLanguageCode)
    {
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo culture = CultureInfo.GetCultureInfo(cultureName);
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            string dir = Path.Combine(Path.GetTempPath(), "AppleMusicOverlay.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "settings.json");
            var service = new OverlaySettingsService(path);

            OverlaySettings loaded = service.Load();

            Assert.Equal(expectedLanguageCode, loaded.LanguageCode);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }
}
