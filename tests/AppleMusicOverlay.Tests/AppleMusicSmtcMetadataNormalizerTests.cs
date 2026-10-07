using AppleMusicOverlay.Services;

namespace AppleMusicOverlay.Tests;

public sealed class AppleMusicSmtcMetadataNormalizerTests
{
    private const string AppleMusicWindowsApp = "AppleInc.AppleMusicWin_nzyj5cx40ttqa!App";

    [Theory]
    [InlineData("Finn Wolfhard \u2014 Fire From The Hip")]
    [InlineData("Finn Wolfhard \u2013 Fire From The Hip")]
    [InlineData("Finn Wolfhard - Fire From The Hip")]
    public void NormalizeArtistRemovesMatchingAlbumSuffixForAppleMusicWindowsApp(string artist)
    {
        string result = AppleMusicSmtcMetadataNormalizer.NormalizeArtist(
            AppleMusicWindowsApp,
            artist,
            "Fire From The Hip");

        Assert.Equal("Finn Wolfhard", result);
    }

    [Theory]
    [InlineData("Spotify.exe")]
    [InlineData("Microsoft.MicrosoftEdge.Stable_8wekyb3d8bbwe!MSEdge")]
    [InlineData("Google.Chrome")]
    public void NormalizeArtistLeavesOtherSourcesUnchanged(string sourceAppUserModelId)
    {
        const string artist = "Finn Wolfhard \u2014 Fire From The Hip";

        string result = AppleMusicSmtcMetadataNormalizer.NormalizeArtist(
            sourceAppUserModelId,
            artist,
            "Fire From The Hip");

        Assert.Equal(artist, result);
    }

    [Fact]
    public void NormalizeArtistLeavesDifferentSuffixUnchanged()
    {
        const string artist = "Artist \u2014 Live Ensemble";

        string result = AppleMusicSmtcMetadataNormalizer.NormalizeArtist(
            AppleMusicWindowsApp,
            artist,
            "Studio Album");

        Assert.Equal(artist, result);
    }

    [Theory]
    [InlineData("Wolf Alice \u2014 The Clearing (B Sides)", "Wolf Alice")]
    [InlineData("Courteeners \u2014 Plus One Forever - Single", "Courteeners")]
    [InlineData("Orla Gartland \u2014 Trying: Season 5 (Apple Original Series Soundtrack)", "Orla Gartland")]
    [InlineData("Artist \u2014 Album \u2014 Deluxe", "Artist")]
    public void NormalizeArtistRemovesAppleMusicSuffixWhenAlbumIsMissing(string artist, string expected)
    {
        string result = AppleMusicSmtcMetadataNormalizer.NormalizeArtist(
            AppleMusicWindowsApp,
            artist,
            string.Empty);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void NormalizeArtistLeavesAppleMusicArtistWithoutDelimiterUnchanged()
    {
        const string artist = "Artist Without Album Suffix";

        string result = AppleMusicSmtcMetadataNormalizer.NormalizeArtist(
            AppleMusicWindowsApp,
            artist,
            string.Empty);

        Assert.Equal(artist, result);
    }

    [Fact]
    public void NormalizeArtistLeavesOtherSourceUnchangedWhenAlbumIsMissing()
    {
        const string artist = "Artist \u2014 Album";

        string result = AppleMusicSmtcMetadataNormalizer.NormalizeArtist(
            "Spotify.exe",
            artist,
            string.Empty);

        Assert.Equal(artist, result);
    }
}
