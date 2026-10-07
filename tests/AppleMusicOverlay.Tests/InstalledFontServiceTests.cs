using AppleMusicOverlay.Services;

namespace AppleMusicOverlay.Tests;

public sealed class InstalledFontServiceTests
{
    [Fact]
    public void DefaultOptionIsAlwaysAvailable()
    {
        OverlayTrackFontAvailability availability = InstalledFontService.DetectForTesting([]);

        IReadOnlyList<OverlayTrackFontOption> options = availability.CreateOptions();

        OverlayTrackFontOption option = Assert.Single(options);
        Assert.Equal(OverlayTrackFontIds.Default, option.Id);
        Assert.True(availability.IsAvailable(OverlayTrackFontIds.Default));
    }

    [Fact]
    public void UnsupportedFontsDoNotShowSpotifyMixOrSfPro()
    {
        OverlayTrackFontAvailability availability = InstalledFontService.DetectForTesting(
            ["Spotify Circular", "SF Pro Rounded", "SF Mono", "SF Symbols", "New York"]);

        IReadOnlyList<string> optionIds = availability.CreateOptions().Select(option => option.Id).ToArray();

        Assert.DoesNotContain(OverlayTrackFontIds.SpotifyMix, optionIds);
        Assert.DoesNotContain(OverlayTrackFontIds.SfPro, optionIds);
    }

    [Theory]
    [InlineData("Spotify Mix")]
    [InlineData("SpotifyMix")]
    [InlineData("spotify mix text")]
    [InlineData(" Spotify Mix Display ")]
    public void SpotifyMixOptionAppearsForSupportedFamilyNames(string fontFamilyName)
    {
        OverlayTrackFontAvailability availability = InstalledFontService.DetectForTesting([fontFamilyName]);

        Assert.Contains(availability.CreateOptions(), option => option.Id == OverlayTrackFontIds.SpotifyMix);
    }

    [Fact]
    public void SpotifyMixDoesNotUseBroadSpotifyContainsMatch()
    {
        OverlayTrackFontAvailability availability = InstalledFontService.DetectForTesting(["Spotify Circular"]);

        Assert.DoesNotContain(availability.CreateOptions(), option => option.Id == OverlayTrackFontIds.SpotifyMix);
    }

    [Fact]
    public void SfProOptionAppearsForSfProText()
    {
        OverlayTrackFontAvailability availability = InstalledFontService.DetectForTesting(["SF Pro Text"]);

        Assert.Contains(availability.CreateOptions(), option => option.Id == OverlayTrackFontIds.SfPro);
    }

    [Fact]
    public void SfProRoundedDoesNotCountAsStandardSfPro()
    {
        OverlayTrackFontAvailability availability = InstalledFontService.DetectForTesting(["SF Pro Rounded"]);

        Assert.DoesNotContain(availability.CreateOptions(), option => option.Id == OverlayTrackFontIds.SfPro);
    }

    [Fact]
    public void OptionsKeepStableDisplayOrder()
    {
        OverlayTrackFontAvailability availability = InstalledFontService.DetectForTesting(["SF Pro Text", "SpotifyMix"]);

        IReadOnlyList<string> optionIds = availability.CreateOptions().Select(option => option.Id).ToArray();

        Assert.Equal(
            [OverlayTrackFontIds.Default, OverlayTrackFontIds.SpotifyMix, OverlayTrackFontIds.SfPro],
            optionIds);
    }

    [Fact]
    public void SavedFontFallsBackToDefaultWhenUnavailable()
    {
        OverlayTrackFontAvailability availability = InstalledFontService.DetectForTesting([]);

        Assert.Equal(OverlayTrackFontIds.Default, availability.NormalizeSelection(OverlayTrackFontIds.SpotifyMix));
        Assert.Equal(OverlayTrackFontIds.Default, availability.NormalizeSelection(OverlayTrackFontIds.SfPro));
    }

    [Fact]
    public void UnknownFontSettingFallsBackToDefault()
    {
        OverlayTrackFontAvailability availability = InstalledFontService.DetectForTesting(["SF Pro Text"]);

        Assert.Equal(OverlayTrackFontIds.Default, availability.NormalizeSelection("broken"));
        Assert.Equal(OverlayTrackFontIds.Default, availability.NormalizeSelection(null));
    }

    [Fact]
    public void SfProTextResolvesForTitleAndArtistWhenItIsOnlyAvailableSfProFamily()
    {
        OverlayTrackFontAvailability availability = InstalledFontService.DetectForTesting(["SF Pro Text"]);

        ResolvedOverlayTrackFont font = availability.Resolve(OverlayTrackFontIds.SfPro);

        Assert.Equal("SF Pro Text", font.TitleFontFamily);
        Assert.Equal("SF Pro Text", font.ArtistFontFamily);
    }

    [Fact]
    public void SpotifyMixUsesTitleAndArtistPriorities()
    {
        OverlayTrackFontAvailability availability = InstalledFontService.DetectForTesting(["Spotify Mix Display", "Spotify Mix Text"]);

        ResolvedOverlayTrackFont font = availability.Resolve(OverlayTrackFontIds.SpotifyMix);

        Assert.Equal("Spotify Mix Display", font.TitleFontFamily);
        Assert.Equal("Spotify Mix Text", font.ArtistFontFamily);
    }

    [Fact]
    public void ResolvedFontFamilyListIncludesSystemFallbacks()
    {
        OverlayTrackFontAvailability availability = InstalledFontService.DetectForTesting(["SF Pro Text"]);

        string titleFontFamily = availability.ResolveFontFamilyList(OverlayTrackFontIds.SfPro, isTitle: true);

        Assert.StartsWith("SF Pro Text", titleFontFamily);
        Assert.Contains("Microsoft YaHei UI", titleFontFamily);
        Assert.Contains("Segoe UI Symbol", titleFontFamily);
        Assert.Contains("Segoe UI Emoji", titleFontFamily);
    }
}
