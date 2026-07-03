using AppleMusicOverlay.Models;
using AppleMusicOverlay.Services;

namespace AppleMusicOverlay.Tests;

public sealed class TrackMonitorTests
{
    [Fact]
    public async Task PollOnceRaisesChangedForNewTracksAndRefreshesCoverUpdates()
    {
        var first = new TrackInfo("First", "Artist", [1], "AppleMusic", TimeSpan.FromMinutes(3), true);
        var same = first with { CoverBytes = [9], IsPlaying = false };
        var second = first with { Title = "Second", CoverBytes = [2] };
        var service = new FakeMediaSessionService(first, first, same, second, second, null);
        var monitor = new TrackMonitor(service, settleDelay: TimeSpan.Zero);
        var reads = new List<TrackInfo?>();
        var changes = new List<TrackInfo>();
        var refreshes = new List<TrackInfo>();
        monitor.TrackRead += (_, track) => reads.Add(track);
        monitor.TrackChanged += (_, track) => changes.Add(track);
        monitor.TrackRefreshed += (_, track) => refreshes.Add(track);

        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();

        Assert.Equal(4, reads.Count);
        Assert.Equal([first, second], changes);
        Assert.Equal([same], refreshes);
    }

    [Fact]
    public async Task PollOnceRefreshesTrackAfterIdentityChangesBeforeRaisingChanged()
    {
        byte[] staleCover = [1];
        byte[] freshCover = [2];
        var first = new TrackInfo("First", "Artist", staleCover, "AppleMusic", TimeSpan.FromMinutes(3), true);
        var secondWithStaleCover = new TrackInfo("Second", "Artist", staleCover, "AppleMusic", TimeSpan.FromMinutes(3), true);
        var secondWithFreshCover = new TrackInfo("Second", "Artist", freshCover, "AppleMusic", TimeSpan.FromMinutes(3), true);
        var service = new FakeMediaSessionService(first, first, secondWithStaleCover, secondWithFreshCover);
        var monitor = new TrackMonitor(service, settleDelay: TimeSpan.Zero);
        var changes = new List<TrackInfo>();
        var refreshes = new List<TrackInfo>();
        monitor.TrackChanged += (_, track) => changes.Add(track);
        monitor.TrackRefreshed += (_, track) => refreshes.Add(track);

        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();

        Assert.Equal([first, secondWithFreshCover], changes);
        Assert.Empty(refreshes);
    }

    [Fact]
    public async Task PollOnceUsesSettledTrackWhenIdentityChangesDuringSettle()
    {
        var first = new TrackInfo("First", "Artist", [1], "AppleMusic", TimeSpan.FromMinutes(3), true);
        var transitional = new TrackInfo("Loading", "", null, "AppleMusic", TimeSpan.FromMinutes(3), true);
        var settled = new TrackInfo("Second", "Artist", [2], "AppleMusic", TimeSpan.FromMinutes(3), true);
        var service = new FakeMediaSessionService(first, first, transitional, settled);
        var monitor = new TrackMonitor(service, settleDelay: TimeSpan.Zero);
        var changes = new List<TrackInfo>();
        monitor.TrackChanged += (_, track) => changes.Add(track);

        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();

        Assert.Equal([first, settled], changes);
    }

    [Fact]
    public async Task PollOnceDoesNotRaiseChangedForUnknownTransitionBeforeRealTrack()
    {
        var first = new TrackInfo("First", "Artist", [1], "AppleMusic", TimeSpan.FromMinutes(3), true);
        var unknown = new TrackInfo("Unknown Track", "Unknown Artist", null, "AppleMusic", TimeSpan.FromMinutes(3), true);
        var second = new TrackInfo("Second", "Artist", [2], "AppleMusic", TimeSpan.FromMinutes(3), true);
        var service = new FakeMediaSessionService(first, first, unknown, unknown, second, second);
        var monitor = new TrackMonitor(service, settleDelay: TimeSpan.Zero);
        var reads = new List<TrackInfo?>();
        var changes = new List<TrackInfo>();
        monitor.TrackRead += (_, track) => reads.Add(track);
        monitor.TrackChanged += (_, track) => changes.Add(track);

        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();

        Assert.Equal([first, second], changes);
        Assert.DoesNotContain(unknown, reads);
    }

    [Fact]
    public async Task CurrentTrackKeepsLastStableTrackDuringUnknownTransition()
    {
        var first = new TrackInfo("First", "Artist", [1], "AppleMusic", TimeSpan.FromMinutes(3), true);
        var unknown = new TrackInfo("Unknown Track", "Unknown Artist", null, "AppleMusic", TimeSpan.FromMinutes(3), true);
        var second = new TrackInfo("Second", "Artist", [2], "AppleMusic", TimeSpan.FromMinutes(3), true);
        var service = new FakeMediaSessionService(first, first, unknown, unknown, second, second);
        var monitor = new TrackMonitor(service, settleDelay: TimeSpan.Zero);

        Assert.Null(monitor.CurrentTrack);

        await monitor.PollOnceAsync();
        Assert.Equal(first, monitor.CurrentTrack);

        await monitor.PollOnceAsync();
        Assert.Equal(first, monitor.CurrentTrack);

        await monitor.PollOnceAsync();
        Assert.Equal(second, monitor.CurrentTrack);
    }

    [Fact]
    public async Task PollOnceRefreshesWhenArtistArrivesAfterTitle()
    {
        byte[] cover = [1];
        var withoutArtist = new TrackInfo("Song", "Unknown Artist", cover, "AppleMusic", TimeSpan.FromMinutes(3), true);
        var normalizedWithoutArtist = withoutArtist with { Artist = string.Empty };
        var withArtist = withoutArtist with { Artist = "Artist" };
        var service = new FakeMediaSessionService(withoutArtist, withoutArtist, withArtist, withArtist);
        var monitor = new TrackMonitor(service, settleDelay: TimeSpan.Zero);
        var changes = new List<TrackInfo>();
        var refreshes = new List<TrackInfo>();
        monitor.TrackChanged += (_, track) => changes.Add(track);
        monitor.TrackRefreshed += (_, track) => refreshes.Add(track);

        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();

        Assert.Equal([normalizedWithoutArtist], changes);
        Assert.Equal([withArtist], refreshes);
    }

    [Fact]
    public async Task PollOnceDoesNotShowPreviousCoverWhenNewTrackInitiallyKeepsOldCover()
    {
        byte[] oldCover = [1];
        byte[] freshCover = [2];
        var first = new TrackInfo("First", "Artist", oldCover, "AppleMusic", TimeSpan.FromMinutes(3), true);
        var secondWithOldCover = new TrackInfo("Second", "Artist", oldCover, "AppleMusic", TimeSpan.FromMinutes(3), true);
        var secondWithFreshCover = secondWithOldCover with { CoverBytes = freshCover };
        var service = new FakeMediaSessionService(first, first, secondWithOldCover, secondWithOldCover, secondWithFreshCover);
        var monitor = new TrackMonitor(service, settleDelay: TimeSpan.Zero);
        var changes = new List<TrackInfo>();
        var refreshes = new List<TrackInfo>();
        monitor.TrackChanged += (_, track) => changes.Add(track);
        monitor.TrackRefreshed += (_, track) => refreshes.Add(track);

        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();

        Assert.Equal([first, secondWithFreshCover], changes);
        Assert.Empty(refreshes);
    }

    [Fact]
    public async Task PollOnceRaisesChangedOnlyAfterFirstTrackReceivesCover()
    {
        var withoutCover = new TrackInfo("Song", "Artist", null, "AppleMusic", TimeSpan.FromMinutes(3), true);
        var withCover = withoutCover with { CoverBytes = [2] };
        var service = new FakeMediaSessionService(withoutCover, withoutCover, withCover);
        var monitor = new TrackMonitor(service, settleDelay: TimeSpan.Zero);
        var changes = new List<TrackInfo>();
        var refreshes = new List<TrackInfo>();
        monitor.TrackChanged += (_, track) => changes.Add(track);
        monitor.TrackRefreshed += (_, track) => refreshes.Add(track);

        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();

        Assert.Equal([withCover], changes);
        Assert.Empty(refreshes);
    }

    [Fact]
    public async Task PollOnceReportsNullWhenMediaSessionFails()
    {
        var monitor = new TrackMonitor(new ThrowingMediaSessionService());
        var reads = new List<TrackInfo?>();
        monitor.TrackRead += (_, track) => reads.Add(track);

        await monitor.PollOnceAsync();

        Assert.Equal([null], reads);
    }

    [Fact]
    public async Task StartWaitsForPollIntervalBeforeFirstAutomaticRead()
    {
        var service = new CountingMediaSessionService();
        using var monitor = new TrackMonitor(service, TimeSpan.FromSeconds(10));

        monitor.Start();
        await Task.Delay(50);

        Assert.Equal(0, service.ReadCount);
    }

    [Fact]
    public async Task PollOnceSkipsCoverReadAfterStableTrackIsKnown()
    {
        var track = new TrackInfo("Song", "Artist", [1], "AppleMusic", TimeSpan.FromMinutes(3), true);
        var service = new ReadOptionsMediaSessionService(track, track, track);
        var monitor = new TrackMonitor(service, settleDelay: TimeSpan.Zero);

        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();

        Assert.Equal([true, true, false], service.IncludeCoverRequests);
    }

    [Fact]
    public async Task PollOnceRefetchesCoverImmediatelyWhenMetadataOnlyReadFindsNewTrack()
    {
        var first = new TrackInfo("First", "Artist", [1], "AppleMusic", TimeSpan.FromMinutes(3), true);
        var second = new TrackInfo("Second", "Artist", [2], "AppleMusic", TimeSpan.FromMinutes(3), true);
        var service = new ReadOptionsMediaSessionService(first, first, second, second);
        var monitor = new TrackMonitor(service, settleDelay: TimeSpan.Zero);
        var changes = new List<TrackInfo>();
        monitor.TrackChanged += (_, track) => changes.Add(track);

        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();

        Assert.Equal([first, second], changes);
        Assert.Equal([true, true, false, true], service.IncludeCoverRequests);
    }

    [Fact]
    public async Task StartPollsImmediatelyWhenMediaSessionSignalsTrackChange()
    {
        var first = new TrackInfo("First", "Artist", [1], "AppleMusic", TimeSpan.FromMinutes(3), true);
        var second = new TrackInfo("Second", "Artist", [2], "AppleMusic", TimeSpan.FromMinutes(3), true);
        var service = new NotifyingReadOptionsMediaSessionService(first, first, second, second);
        using var monitor = new TrackMonitor(
            service,
            pollInterval: TimeSpan.FromSeconds(30),
            stablePollInterval: TimeSpan.FromSeconds(30),
            settleDelay: TimeSpan.Zero);
        var changes = new List<TrackInfo>();
        monitor.TrackChanged += (_, track) => changes.Add(track);

        await monitor.PollOnceAsync();
        monitor.Start();
        service.RaiseMediaSessionChanged();
        await service.WaitForReadCountAsync(4, TimeSpan.FromSeconds(1));

        Assert.Equal([first, second], changes);
        Assert.Equal([true, true, false, true], service.IncludeCoverRequests);
    }

    private sealed class FakeMediaSessionService : IMediaSessionService
    {
        private readonly Queue<TrackInfo?> _tracks;

        public FakeMediaSessionService(params TrackInfo?[] tracks)
        {
            _tracks = new Queue<TrackInfo?>(tracks);
        }

        public Task<TrackInfo?> GetCurrentTrackAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_tracks.Count == 0 ? null : _tracks.Dequeue());
        }

        public Task NextAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task PreviousAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task TogglePlayPauseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ThrowingMediaSessionService : IMediaSessionService
    {
        public Task<TrackInfo?> GetCurrentTrackAsync(CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("SMTC unavailable");
        }

        public Task NextAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task PreviousAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task TogglePlayPauseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class CountingMediaSessionService : IMediaSessionService
    {
        public int ReadCount { get; private set; }

        public Task<TrackInfo?> GetCurrentTrackAsync(CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return Task.FromResult<TrackInfo?>(null);
        }

        public Task NextAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task PreviousAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task TogglePlayPauseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ReadOptionsMediaSessionService : IMediaSessionService
    {
        private readonly Queue<TrackInfo?> _tracks;

        public ReadOptionsMediaSessionService(params TrackInfo?[] tracks)
        {
            _tracks = new Queue<TrackInfo?>(tracks);
        }

        public List<bool> IncludeCoverRequests { get; } = new();

        public Task<TrackInfo?> GetCurrentTrackAsync(CancellationToken cancellationToken = default)
        {
            IncludeCoverRequests.Add(true);
            return DequeueAsync(includeCover: true);
        }

        public Task<TrackInfo?> GetCurrentTrackAsync(MediaSessionReadOptions options, CancellationToken cancellationToken = default)
        {
            IncludeCoverRequests.Add(options.IncludeCover);
            return DequeueAsync(options.IncludeCover);
        }

        public Task NextAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task PreviousAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task TogglePlayPauseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        private Task<TrackInfo?> DequeueAsync(bool includeCover)
        {
            TrackInfo? track = _tracks.Count == 0 ? null : _tracks.Dequeue();
            return Task.FromResult(track == null || includeCover ? track : track with { CoverBytes = null });
        }
    }

    private sealed class NotifyingReadOptionsMediaSessionService : IMediaSessionService, IMediaSessionChangeNotifier
    {
        private readonly Queue<TrackInfo?> _tracks;
        private TaskCompletionSource<int> _readCountChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public NotifyingReadOptionsMediaSessionService(params TrackInfo?[] tracks)
        {
            _tracks = new Queue<TrackInfo?>(tracks);
        }

        public event EventHandler? MediaSessionChanged;

        public List<bool> IncludeCoverRequests { get; } = new();

        public int ReadCount { get; private set; }

        public Task<TrackInfo?> GetCurrentTrackAsync(CancellationToken cancellationToken = default)
        {
            IncludeCoverRequests.Add(true);
            return DequeueAsync(includeCover: true);
        }

        public Task<TrackInfo?> GetCurrentTrackAsync(MediaSessionReadOptions options, CancellationToken cancellationToken = default)
        {
            IncludeCoverRequests.Add(options.IncludeCover);
            return DequeueAsync(options.IncludeCover);
        }

        public Task NextAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task PreviousAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task TogglePlayPauseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void RaiseMediaSessionChanged()
        {
            MediaSessionChanged?.Invoke(this, EventArgs.Empty);
        }

        public async Task WaitForReadCountAsync(int expectedReadCount, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            while (ReadCount < expectedReadCount)
            {
                Task waitTask = _readCountChanged.Task;
                await waitTask.WaitAsync(cts.Token);
            }
        }

        private Task<TrackInfo?> DequeueAsync(bool includeCover)
        {
            ReadCount++;
            _readCountChanged.TrySetResult(ReadCount);
            _readCountChanged = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            TrackInfo? track = _tracks.Count == 0 ? null : _tracks.Dequeue();
            return Task.FromResult(track == null || includeCover ? track : track with { CoverBytes = null });
        }
    }
}
