using AppleMusicOverlay.Services;

namespace AppleMusicOverlay.Tests;

public sealed class MediaSessionSelectorTests
{
    [Fact]
    public void SelectBestPrefersEdgeAppleMusicOverCurrentChromeVideo()
    {
        var chrome = new MediaSessionCandidate(
            SourceAppUserModelId: "Chrome",
            Title: "A YouTube Video",
            Artist: "YouTube",
            IsPlaying: true,
            IsCurrent: true,
            Index: 0);
        var appleMusic = new MediaSessionCandidate(
            SourceAppUserModelId: "Microsoft.MicrosoftEdge.Stable_8wekyb3d8bbwe!MSEdge",
            Title: "Apple Music Song",
            Artist: "Artist",
            IsPlaying: true,
            IsCurrent: false,
            Index: 1);

        MediaSessionCandidate? selected = MediaSessionSelector.SelectBest([chrome, appleMusic]);

        Assert.Equal(appleMusic, selected);
    }

    [Fact]
    public void SelectBestPrefersExplicitAppleMusicSource()
    {
        var current = new MediaSessionCandidate("Chrome", "Video", "YouTube", true, true, 0);
        var appleMusic = new MediaSessionCandidate("AppleMusicPWA", "Song", "Artist", true, false, 1);

        MediaSessionCandidate? selected = MediaSessionSelector.SelectBest([current, appleMusic]);

        Assert.Equal(appleMusic, selected);
    }

    [Fact]
    public void SelectBestFallsBackToCurrentSessionWhenNoPreferredSourceExists()
    {
        var current = new MediaSessionCandidate("Chrome", "Video", "YouTube", true, true, 0);
        var other = new MediaSessionCandidate("Spotify", "Song", "Artist", true, false, 1);

        MediaSessionCandidate? selected = MediaSessionSelector.SelectBest([current, other]);

        Assert.Equal(current, selected);
    }

    [Fact]
    public void SelectBestUsesPreferredSourceWhenAvailable()
    {
        var edge = new MediaSessionCandidate("MicrosoftEdge", "Song", "Artist", true, false, 0);
        var chrome = new MediaSessionCandidate("Chrome", "Video", "YouTube", true, true, 1);

        MediaSessionCandidate? selected = MediaSessionSelector.SelectBest([edge, chrome], "Chrome");

        Assert.Equal(chrome, selected);
    }

    [Fact]
    public void SelectBestReturnsNullWhenPreferredSourceIsUnavailable()
    {
        var edge = new MediaSessionCandidate("MicrosoftEdge", "Song", "Artist", true, false, 0);
        var chrome = new MediaSessionCandidate("Chrome", "Video", "YouTube", true, true, 1);

        MediaSessionCandidate? selected = MediaSessionSelector.SelectBest([edge, chrome], "Spotify");

        Assert.Null(selected);
    }
}
