using System.Security.Cryptography;
using AppleMusicOverlay.Models;

namespace AppleMusicOverlay.Services;

public sealed class TrackMonitor : IDisposable
{
    private static readonly TimeSpan SettleProbeInterval = TimeSpan.FromMilliseconds(40);
    private readonly IMediaSessionService _mediaSessionService;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _settleDelay;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private string? _lastTrackKey;
    private string? _lastCoverKey;
    private TrackInfo? _lastStableTrack;
    private bool _disposed;

    public event EventHandler<TrackInfo?>? TrackRead;

    public event EventHandler<TrackInfo>? TrackChanged;

    public event EventHandler<TrackInfo>? TrackRefreshed;

    public TrackInfo? CurrentTrack => _lastStableTrack;

    public TrackMonitor(
        IMediaSessionService mediaSessionService,
        TimeSpan? pollInterval = null,
        TimeSpan? settleDelay = null)
    {
        _mediaSessionService = mediaSessionService;
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(300);
        _settleDelay = settleDelay ?? TimeSpan.FromMilliseconds(120);
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

        if (track == null)
        {
            _lastStableTrack = null;
            TrackRead?.Invoke(this, null);
            return;
        }

        track = NormalizeTrack(track);
        string trackKey = TrackIdentity.Create(track);
        string coverKey = CreateCoverKey(track);
        bool metadataUpdated = IsMetadataRefresh(track);
        bool trackChanged = trackKey != _lastTrackKey && !metadataUpdated;
        bool refreshed = metadataUpdated ||
                         (trackKey == _lastTrackKey && coverKey != _lastCoverKey && track.CoverBytes is { Length: > 0 });

        if (!trackChanged && !refreshed)
        {
            PublishRead(track);
            return;
        }

        if (trackChanged)
        {
            track = NormalizeTrack(await SettleTrackAsync(track, _lastCoverKey, cancellationToken));
            if (!IsUsableTrack(track))
            {
                TrackRead?.Invoke(this, _lastStableTrack);
                return;
            }

            trackKey = TrackIdentity.Create(track);
            coverKey = CreateCoverKey(track);
            if (IsMetadataRefresh(track))
            {
                _lastTrackKey = trackKey;
                _lastCoverKey = coverKey;
                PublishRead(track);
                TrackRefreshed?.Invoke(this, track);
                return;
            }

            track = RemovePreviousCover(track, _lastCoverKey);
            coverKey = CreateCoverKey(track);
            if (trackKey == _lastTrackKey)
            {
                if (coverKey != _lastCoverKey && track.CoverBytes is { Length: > 0 })
                {
                    _lastCoverKey = coverKey;
                    PublishRead(track);
                    TrackRefreshed?.Invoke(this, track);
                }
                else
                {
                    PublishRead(track);
                }

                return;
            }

            _lastTrackKey = trackKey;
            _lastCoverKey = coverKey;
            PublishRead(track);
            TrackChanged?.Invoke(this, track);
            return;
        }

        PublishRead(track);
        _lastTrackKey = trackKey;
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

    private async Task<TrackInfo> SettleTrackAsync(TrackInfo initialTrack, string? previousCoverKey, CancellationToken cancellationToken)
    {
        if (_settleDelay <= TimeSpan.Zero)
        {
            return await ReadSettledTrackAsync(initialTrack, cancellationToken);
        }

        if (IsReadyToDisplay(initialTrack, previousCoverKey))
        {
            return initialTrack;
        }

        DateTimeOffset deadline = DateTimeOffset.UtcNow + _settleDelay;
        TrackInfo bestTrack = initialTrack;
        while (DateTimeOffset.UtcNow < deadline)
        {
            TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
            TimeSpan delay = remaining < SettleProbeInterval ? remaining : SettleProbeInterval;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }

            bestTrack = NormalizeTrack(await ReadSettledTrackAsync(bestTrack, cancellationToken));
            if (IsReadyToDisplay(bestTrack, previousCoverKey))
            {
                return bestTrack;
            }
        }

        return bestTrack;
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

    private void PublishRead(TrackInfo track)
    {
        _lastStableTrack = track;
        TrackRead?.Invoke(this, track);
    }

    private static TrackInfo NormalizeTrack(TrackInfo track)
    {
        string title = track.Title.Trim();
        string artist = IsPlaceholderText(track.Artist) ? string.Empty : track.Artist.Trim();
        return track with { Title = title, Artist = artist };
    }

    private static TrackInfo RemovePreviousCover(TrackInfo track, string? previousCoverKey)
    {
        if (track.CoverBytes is { Length: > 0 } &&
            !string.IsNullOrEmpty(previousCoverKey) &&
            CreateCoverKey(track) == previousCoverKey)
        {
            return track with { CoverBytes = null };
        }

        return track;
    }

    private static bool IsReadyToDisplay(TrackInfo track, string? previousCoverKey)
    {
        return IsUsableTrack(track) &&
               track.CoverBytes is { Length: > 0 } &&
               (string.IsNullOrEmpty(previousCoverKey) || CreateCoverKey(track) != previousCoverKey);
    }

    private static bool IsUsableTrack(TrackInfo track)
    {
        return !IsPlaceholderText(track.Title);
    }

    private bool IsMetadataRefresh(TrackInfo track)
    {
        return _lastStableTrack != null &&
               _lastStableTrack.SourceAppId.Equals(track.SourceAppId, StringComparison.OrdinalIgnoreCase) &&
               _lastStableTrack.Title.Equals(track.Title, StringComparison.OrdinalIgnoreCase) &&
               !_lastStableTrack.Artist.Equals(track.Artist, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPlaceholderText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        return value.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("Unknown Track", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("Unknown Artist", StringComparison.OrdinalIgnoreCase);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(TrackMonitor));
        }
    }
}
