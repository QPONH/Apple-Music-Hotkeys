using AppleMusicOverlay.Models;
using AppleMusicOverlay.Services;
using AppleMusicOverlay.ViewModels;

namespace AppleMusicOverlay.Tests;

public sealed class MainViewModelTests
{
    [Fact]
    public void CaptureSourceDefaultsToAutomaticAndRemainsSelectedAfterRefresh()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), "AppleMusicOverlay.Tests", Guid.NewGuid().ToString("N"), "settings.json");
        var viewModel = new MainViewModel(new OverlaySettingsService(settingsPath));
        var changedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        viewModel.ReplaceCaptureSources(
        [
            new MediaSessionCandidate("cloudmusic.exe", "Song", "Artist", true, true, 0)
        ]);

        Assert.Equal(string.Empty, viewModel.CaptureSourceAppUserModelId);
        Assert.Equal(string.Empty, viewModel.CaptureSources[0].SourceAppUserModelId);
        Assert.Contains(nameof(MainViewModel.CaptureSourceAppUserModelId), changedProperties);
    }

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

    [Fact]
    public void SavePersistsSettingsWithoutReplacingLongLivedStatusText()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), "AppleMusicOverlay.Tests", Guid.NewGuid().ToString("N"), "settings.json");
        var viewModel = new MainViewModel(new OverlaySettingsService(settingsPath));
        viewModel.ApplyTrack(new TrackInfo("Song", "Artist", null, "Source", TimeSpan.FromMinutes(3), true));

        viewModel.Save();

        Assert.Equal("已连接：Source", viewModel.StatusText);
    }

    [Fact]
    public void ApplyTrackDoesNotRefreshBindingsWhenVisibleTrackStateIsUnchanged()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), "AppleMusicOverlay.Tests", Guid.NewGuid().ToString("N"), "settings.json");
        var viewModel = new MainViewModel(new OverlaySettingsService(settingsPath));
        var track = new TrackInfo("Song", "Artist", [1], "Source", TimeSpan.FromMinutes(3), true);
        var changedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        viewModel.ApplyTrack(track);
        changedProperties.Clear();
        viewModel.ApplyTrack(track with { CoverBytes = [2] });

        Assert.Empty(changedProperties);
    }
}
