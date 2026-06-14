using AppleMusicOverlay.Models;
using AppleMusicOverlay.Services;
using AppleMusicOverlay.ViewModels;

namespace AppleMusicOverlay.Tests;

public sealed class MainViewModelTests
{
    [Fact]
    public void TransportButtonTextShowsPauseWhenTrackIsPlaying()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), "AppleMusicOverlay.Tests", Guid.NewGuid().ToString("N"), "settings.json");
        var viewModel = new MainViewModel(new OverlaySettingsService(settingsPath));

        viewModel.ApplyTrack(new TrackInfo("Song", "Artist", null, "Source", TimeSpan.FromMinutes(3), true));

        Assert.Equal("暂停", viewModel.TransportButtonText);
    }

    [Fact]
    public void TransportButtonTextShowsPlayWhenTrackIsPausedOrUnknown()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), "AppleMusicOverlay.Tests", Guid.NewGuid().ToString("N"), "settings.json");
        var viewModel = new MainViewModel(new OverlaySettingsService(settingsPath));

        Assert.Equal("播放", viewModel.TransportButtonText);

        viewModel.ApplyTrack(new TrackInfo("Song", "Artist", null, "Source", TimeSpan.FromMinutes(3), false));

        Assert.Equal("播放", viewModel.TransportButtonText);
    }
}
