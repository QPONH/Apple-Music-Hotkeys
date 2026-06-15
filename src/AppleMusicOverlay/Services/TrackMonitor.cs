using System.Security.Cryptography;
using AppleMusicOverlay.Models;

namespace AppleMusicOverlay.Services;

public sealed class TrackMonitor : IDisposable
{
    private readonly IMediaSessionService _mediaSessionService;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _settleDelay;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private string? _lastTrackKey;
    private string? _lastCoverKey;
    private bool _disposed;

    public event EventHandler<TrackInfo?>? TrackRead;

    public event EventHandler<TrackInfo>? TrackChanged;

    public event EventHandler<TrackInfo>? TrackRefreshed;

    public TrackMonitor(
        IMediaSessionService mediaSessionService,
        TimeSpan? pollInterval = null,
        TimeSpan? settleDelay = null)
    {
        _mediaSessionService = mediaSessionService;
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(800);
        _settleDelay = settleDelay ?? TimeSpan.FromMilliseconds(280);
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
        TrackInfo? track;
        try
        {
            track = await _mediaSessionService.GetCurrentTrackAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            TrackRead?.Invoke(this, null);
            return;
        }

        TrackRead?.Invoke(this, track);

        if (track == null)
        {
            return;
        }

        string trackKey = TrackIdentity.Create(track);
        string coverKey = CreateCoverKey(track);
        bool trackChanged = trackKey != _lastTrackKey;
        bool coverUpdated = trackKey == _lastTrackKey && coverKey != _lastCoverKey && track.CoverBytes is { Length: > 0 };

        if (!trackChanged && !coverUpdated)
        {
            return;
        }

        if (trackChanged)
        {
            track = await SettleTrackAsync(track, cancellationToken);
            trackKey = TrackIdentity.Create(track);
            coverKey = CreateCoverKey(track);
            _lastTrackKey = trackKey;
            _lastCoverKey = coverKey;
            TrackChanged?.Invoke(this, track);
            return;
        }

        _lastCoverKey = coverKey;
        TrackRefreshed?.Invoke(this, track);
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
                await Task.Delay(_pollInterval, cancellationToken);
                await PollOnceAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task<TrackInfo> SettleTrackAsync(TrackInfo initialTrack, CancellationToken cancellationToken)
    {
        if (_settleDelay <= TimeSpan.Zero)
        {
            return await ReadSettledTrackAsync(initialTrack, cancellationToken);
        }

        await Task.Delay(_settleDelay, cancellationToken);
        return await ReadSettledTrackAsync(initialTrack, cancellationToken);
    }

    private async Task<TrackInfo> ReadSettledTrackAsync(TrackInfo initialTrack, CancellationToken cancellationToken)
    {
        try
        {
            TrackInfo? settledTrack = await _mediaSessionService.GetCurrentTrackAsync(cancellationToken);
            if (settledTrack != null)
            {
                return settledTrack;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
        }

        return initialTrack;
    }

    private static string CreateCoverKey(TrackInfo track)
    {
        if (track.CoverBytes is not { Length: > 0 } coverBytes)
        {
            return string.Empty;
        }

        return Convert.ToHexString(SHA256.HashData(coverBytes));
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(TrackMonitor));
        }
    }
}
