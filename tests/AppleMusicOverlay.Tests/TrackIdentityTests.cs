using AppleMusicOverlay.Models;
using AppleMusicOverlay.Services;

namespace AppleMusicOverlay.Tests;

public sealed class TrackIdentityTests
{
    [Fact]
    public void CreateIgnoresCoverAndPlaybackState()
    {
        var first = new TrackInfo("  Song Name  ", "Artist", null, "App", TimeSpan.FromMinutes(3), true);
        var second = new TrackInfo("Song Name", "Artist", [1, 2, 3], "App", TimeSpan.FromMinutes(3), false);

        Assert.Equal(TrackIdentity.Create(first), TrackIdentity.Create(second));
    }

    [Fact]
    public void CreateChangesWhenTitleArtistOrSourceChanges()
    {
        var source = new TrackInfo("Song", "Artist", null, "App", TimeSpan.FromMinutes(3), true);

        Assert.NotEqual(TrackIdentity.Create(source), TrackIdentity.Create(source with { Title = "Other" }));
        Assert.NotEqual(TrackIdentity.Create(source), TrackIdentity.Create(source with { Artist = "Other" }));
        Assert.NotEqual(TrackIdentity.Create(source), TrackIdentity.Create(source with { SourceAppId = "OtherApp" }));
    }
}
