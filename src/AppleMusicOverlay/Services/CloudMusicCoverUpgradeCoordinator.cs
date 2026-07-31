namespace AppleMusicOverlay.Services;

public sealed class CloudMusicCoverUpgradeCoordinator
{
    private const int MaximumResolutionAttempts = 3;
    private static readonly TimeSpan ResolutionRetryDelay = TimeSpan.FromMilliseconds(550);
    private readonly ICloudMusicCoverResolver _resolver;
    private readonly object _gate = new();
    private CancellationTokenSource? _activeResolutionCts;
    private string? _activeContextKey;
    private long _revision;

    public CloudMusicCoverUpgradeCoordinator(ICloudMusicCoverResolver resolver)
    {
        _resolver = resolver;
    }

    public event EventHandler? CoverUpgraded;

    public bool IsSupportedSource(string sourceAppId)
    {
        return _resolver.IsSupportedSource(sourceAppId);
    }

    public byte[]? SelectCover(
        string title,
        string artist,
        TimeSpan duration,
        bool includeCover = true)
    {
        if (!includeCover)
        {
            return null;
        }

        byte[]? cachedCover = _resolver.TryGetCachedCover(
            title,
            artist,
            duration);
        if (cachedCover is { Length: > 0 })
        {
            CancelPendingResolution();
            return cachedCover;
        }

        StartResolution(title, artist, duration);
        return null;
    }

    public void Reset()
    {
        CancelPendingResolution();
    }

    private void StartResolution(
        string title,
        string artist,
        TimeSpan duration)
    {
        string contextKey = CreateContextKey(title, artist, duration);
        CancellationTokenSource? previousResolutionCts;
        CancellationTokenSource currentResolutionCts;
        long revision;

        lock (_gate)
        {
            if (contextKey.Equals(_activeContextKey, StringComparison.Ordinal))
            {
                return;
            }

            previousResolutionCts = _activeResolutionCts;
            currentResolutionCts = new CancellationTokenSource();
            _activeResolutionCts = currentResolutionCts;
            _activeContextKey = contextKey;
            revision = ++_revision;
        }

        Cancel(previousResolutionCts);
        _ = ResolveCurrentContextAsync(
            revision,
            title,
            artist,
            duration,
            currentResolutionCts);
    }

    private async Task ResolveCurrentContextAsync(
        long revision,
        string title,
        string artist,
        TimeSpan duration,
        CancellationTokenSource resolutionCts)
    {
        bool upgraded = false;
        try
        {
            for (int attempt = 0; attempt < MaximumResolutionAttempts; attempt++)
            {
                upgraded = await _resolver.WarmCacheAsync(
                    title,
                    artist,
                    duration,
                    resolutionCts.Token);
                if (upgraded || attempt == MaximumResolutionAttempts - 1)
                {
                    break;
                }

                await Task.Delay(
                    ResolutionRetryDelay * (attempt + 1),
                    resolutionCts.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }

        bool shouldNotify = false;
        lock (_gate)
        {
            if (revision == _revision)
            {
                _activeResolutionCts = null;
                _activeContextKey = null;
                shouldNotify = upgraded && !resolutionCts.IsCancellationRequested;
            }
        }

        resolutionCts.Dispose();
        if (shouldNotify)
        {
            CoverUpgraded?.Invoke(this, EventArgs.Empty);
        }
    }

    private void CancelPendingResolution()
    {
        CancellationTokenSource? resolutionCts;
        lock (_gate)
        {
            if (_activeResolutionCts == null)
            {
                return;
            }

            resolutionCts = _activeResolutionCts;
            _activeResolutionCts = null;
            _activeContextKey = null;
            _revision++;
        }

        Cancel(resolutionCts);
    }

    private static void Cancel(CancellationTokenSource? resolutionCts)
    {
        if (resolutionCts == null)
        {
            return;
        }

        try
        {
            resolutionCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static string CreateContextKey(
        string title,
        string artist,
        TimeSpan duration)
    {
        return string.Concat(
            CloudMusicPlayingListArtworkFinder.NormalizeIdentity(title),
            "\u001f",
            CloudMusicPlayingListArtworkFinder.NormalizeIdentity(artist),
            "\u001f",
            duration.Ticks);
    }
}
