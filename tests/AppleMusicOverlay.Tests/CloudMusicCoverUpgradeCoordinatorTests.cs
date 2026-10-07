using AppleMusicOverlay.Services;

namespace AppleMusicOverlay.Tests;

public sealed class CloudMusicCoverUpgradeCoordinatorTests
{
    [Fact]
    public void MetadataOnlyReadDoesNotQueryOrWarmCloudMusicCover()
    {
        byte[] cachedCover = [1, 2, 3];
        var resolver = new ControlledCoverResolver { CachedCover = cachedCover };
        var coordinator = new CloudMusicCoverUpgradeCoordinator(resolver);

        byte[]? selectedCover = coordinator.SelectCover(
            "Song",
            "Artist",
            TimeSpan.FromMinutes(3),
            includeCover: false);

        Assert.Null(selectedCover);
        Assert.Equal(0, resolver.CacheLookupCount);
        Assert.Empty(resolver.Requests);
    }

    [Fact]
    public void SelectCoverNeverReturnsUnverifiedSmtcArtwork()
    {
        var resolver = new ControlledCoverResolver();
        var coordinator = new CloudMusicCoverUpgradeCoordinator(resolver);

        byte[]? selectedCover = coordinator.SelectCover(
            "New Song",
            "New Artist",
            TimeSpan.FromMinutes(3));

        Assert.Null(selectedCover);
        Assert.Single(resolver.Requests);
    }

    [Fact]
    public void SelectCoverReturnsCachedUpgradeWithoutStartingAnotherResolution()
    {
        byte[] cachedCover = [4, 5, 6];
        var resolver = new ControlledCoverResolver { CachedCover = cachedCover };
        var coordinator = new CloudMusicCoverUpgradeCoordinator(resolver);

        byte[]? selectedCover = coordinator.SelectCover(
            "Song",
            "Artist",
            TimeSpan.FromMinutes(3));

        Assert.Same(cachedCover, selectedCover);
        Assert.Empty(resolver.Requests);
    }

    [Fact]
    public async Task StaleResolutionIsCancelledAndCannotRefreshTheNewTrack()
    {
        var resolver = new ControlledCoverResolver();
        var coordinator = new CloudMusicCoverUpgradeCoordinator(resolver);
        int notifications = 0;
        var currentNotification = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.CoverUpgraded += (_, _) =>
        {
            notifications++;
            currentNotification.TrySetResult();
        };

        byte[]? firstCover = coordinator.SelectCover(
            "First",
            "Artist",
            TimeSpan.FromMinutes(3));
        byte[]? secondCover = coordinator.SelectCover(
            "Second",
            "Artist",
            TimeSpan.FromMinutes(4));

        Assert.Null(firstCover);
        Assert.Null(secondCover);
        Assert.Equal(2, resolver.Requests.Count);
        Assert.True(resolver.Requests[0].CancellationToken.IsCancellationRequested);

        resolver.Requests[0].Completion.TrySetResult(true);
        await Task.Delay(20);
        Assert.Equal(0, notifications);

        resolver.Requests[1].Completion.TrySetResult(true);
        await currentNotification.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, notifications);
    }

    private sealed class ControlledCoverResolver : ICloudMusicCoverResolver
    {
        public byte[]? CachedCover { get; init; }

        public int CacheLookupCount { get; private set; }

        public List<ResolutionRequest> Requests { get; } = [];

        public bool IsSupportedSource(string sourceAppId)
        {
            return sourceAppId.Equals("cloudmusic.exe", StringComparison.OrdinalIgnoreCase);
        }

        public byte[]? TryGetCachedCover(
            string title,
            string artist,
            TimeSpan duration)
        {
            CacheLookupCount++;
            return CachedCover;
        }

        public Task<bool> WarmCacheAsync(
            string title,
            string artist,
            TimeSpan duration,
            CancellationToken cancellationToken = default)
        {
            var completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Requests.Add(new ResolutionRequest(completion, cancellationToken));
            return completion.Task;
        }
    }

    private sealed record ResolutionRequest(
        TaskCompletionSource<bool> Completion,
        CancellationToken CancellationToken);
}
