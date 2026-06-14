using AppleMusicOverlay.Models;

namespace AppleMusicOverlay.Services;

public sealed class TrackMonitor : IDisposable
{
    private readonly IMediaSessionService _mediaSessionService;
    private readonly TimeSpan _pollInterval;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private string? _lastTrackKey;
    private bool _disposed;

    public event EventHandler<TrackInfo?>? TrackRead;

    public event EventHandler<TrackInfo>? TrackChanged;

    public TrackMonitor(IMediaSessionService mediaSessionService, TimeSpan? pollInterval = null)
    {
        _mediaSessionService = mediaSessionService;
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(800);
    }

    public void Start()
    {
        ThrowIfDisposed();
        if (_cts != null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => RunAsync(_cts.Token));
    }

    public async Task PollOnceAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        TrackInfo? track = await _mediaSessionService.GetCurrentTrackAsync(cancellationToken);
        TrackRead?.Invoke(this, track);

        if (track == null)
        {
            return;
        }

        string key = TrackIdentity.Create(track);
        if (key == _lastTrackKey)
        {
            return;
        }

        _lastTrackKey = key;
        TrackChanged?.Invoke(this, track);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _loopTask = null;
        _disposed = true;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(cancellationToken);
                await Task.Delay(_pollInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                TrackRead?.Invoke(this, null);
                await Task.Delay(_pollInterval, cancellationToken);
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(TrackMonitor));
        }
    }
}
