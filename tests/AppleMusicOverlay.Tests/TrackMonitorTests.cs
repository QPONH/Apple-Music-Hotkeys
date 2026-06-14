using AppleMusicOverlay.Models;
using AppleMusicOverlay.Services;

namespace AppleMusicOverlay.Tests;

public sealed class TrackMonitorTests
{
    [Fact]
    public async Task PollOnceRaisesChangedOnlyWhenTrackIdentityChanges()
    {
        var first = new TrackInfo("First", "Artist", null, "AppleMusic", TimeSpan.FromMinutes(3), true);
        var same = first with { CoverBytes = [9], IsPlaying = false };
        var second = first with { Title = "Second" };
        var service = new FakeMediaSessionService(first, same, second, null);
        var monitor = new TrackMonitor(service);
        var reads = new List<TrackInfo?>();
        var changes = new List<TrackInfo>();
        monitor.TrackRead += (_, track) => reads.Add(track);
        monitor.TrackChanged += (_, track) => changes.Add(track);

        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();

        Assert.Equal(4, reads.Count);
        Assert.Equal([first, second], changes);
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
}
