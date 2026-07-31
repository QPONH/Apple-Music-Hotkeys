using System.IO;
using AppleMusicOverlay.Models;

namespace AppleMusicOverlay.Services;

public sealed record CloudMusicFavoriteStateChangedEventArgs(
    TrackInfo Track,
    OverlayFavoriteVisualState PreviousState,
    OverlayFavoriteVisualState CurrentState);

public sealed class CloudMusicFavoriteStateMonitor : IDisposable
{
    private static readonly TimeSpan DatabaseSettleDelay = TimeSpan.FromMilliseconds(120);
    private readonly CloudMusicFavoriteStateService _stateService;
    private readonly object _gate = new();
    private readonly Timer _periodicTimer;
    private readonly Timer _databaseChangeTimer;
    private readonly FileSystemWatcher? _databaseWatcher;
    private TrackInfo? _currentTrack;
    private string? _currentTrackIdentity;
    private OverlayFavoriteVisualState? _lastState;
    private int _refreshInProgress;
    private int _forceRefreshRequested;
    private bool _disposed;

    public CloudMusicFavoriteStateMonitor(
        CloudMusicFavoriteStateService stateService,
        string? libraryDirectory = null)
    {
        _stateService = stateService;
        _periodicTimer = new Timer(
            _ => QueueRefresh(forceRefresh: false),
            null,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));
        _databaseChangeTimer = new Timer(
            _ => QueueRefresh(forceRefresh: true),
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);

        string directory = libraryDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NetEase",
            "CloudMusic",
            "Library");
        if (!Directory.Exists(directory))
        {
            return;
        }

        try
        {
            _databaseWatcher = new FileSystemWatcher(directory, "webdb.dat*")
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName |
                               NotifyFilters.LastWrite |
                               NotifyFilters.Size
            };
            _databaseWatcher.Changed += DatabaseWatcher_Changed;
            _databaseWatcher.Created += DatabaseWatcher_Changed;
            _databaseWatcher.Renamed += DatabaseWatcher_Renamed;
            _databaseWatcher.EnableRaisingEvents = true;
        }
        catch
        {
            _databaseWatcher?.Dispose();
            _databaseWatcher = null;
        }
    }

    public event EventHandler<CloudMusicFavoriteStateChangedEventArgs>? StateChanged;

    public void SetKnownState(
        TrackInfo track,
        OverlayFavoriteVisualState state)
    {
        if (state == OverlayFavoriteVisualState.Unavailable ||
            !_stateService.IsSupportedSource(track.SourceAppId))
        {
            return;
        }

        string identity = TrackIdentity.Create(track);
        lock (_gate)
        {
            if (_disposed ||
                !string.Equals(identity, _currentTrackIdentity, StringComparison.Ordinal))
            {
                return;
            }

            _lastState = state;
        }
    }

    public void UpdateTrack(TrackInfo? track)
    {
        string? identity = track != null &&
                           _stateService.IsSupportedSource(track.SourceAppId)
            ? TrackIdentity.Create(track)
            : null;
        bool changed;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            changed = !string.Equals(identity, _currentTrackIdentity, StringComparison.Ordinal);
            _currentTrack = identity == null ? null : track;
            _currentTrackIdentity = identity;
            if (changed)
            {
                _lastState = null;
            }
        }

        if (identity != null && changed)
        {
            QueueRefresh(forceRefresh: false);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _currentTrack = null;
            _currentTrackIdentity = null;
        }

        _periodicTimer.Dispose();
        _databaseChangeTimer.Dispose();
        if (_databaseWatcher != null)
        {
            _databaseWatcher.EnableRaisingEvents = false;
            _databaseWatcher.Changed -= DatabaseWatcher_Changed;
            _databaseWatcher.Created -= DatabaseWatcher_Changed;
            _databaseWatcher.Renamed -= DatabaseWatcher_Renamed;
            _databaseWatcher.Dispose();
        }
    }

    private void DatabaseWatcher_Changed(object sender, FileSystemEventArgs e)
    {
        DebounceDatabaseChange();
    }

    private void DatabaseWatcher_Renamed(object sender, RenamedEventArgs e)
    {
        DebounceDatabaseChange();
    }

    private void DebounceDatabaseChange()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _databaseChangeTimer.Change(DatabaseSettleDelay, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void QueueRefresh(bool forceRefresh)
    {
        if (forceRefresh)
        {
            Interlocked.Exchange(ref _forceRefreshRequested, 1);
        }

        if (Interlocked.Exchange(ref _refreshInProgress, 1) != 0)
        {
            return;
        }

        _ = RefreshPendingAsync(forceRefresh);
    }

    private async Task RefreshPendingAsync(bool initialForceRefresh)
    {
        try
        {
            bool forceRefresh = initialForceRefresh;
            while (true)
            {
                if (Interlocked.Exchange(ref _forceRefreshRequested, 0) != 0)
                {
                    forceRefresh = true;
                }

                await RefreshOnceAsync(forceRefresh);
                forceRefresh = false;

                if (Volatile.Read(ref _forceRefreshRequested) == 0)
                {
                    return;
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _refreshInProgress, 0);
            if (Volatile.Read(ref _forceRefreshRequested) != 0)
            {
                QueueRefresh(forceRefresh: true);
            }
        }
    }

    private async Task RefreshOnceAsync(bool forceRefresh)
    {
        TrackInfo? track;
        string? identity;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            track = _currentTrack;
            identity = _currentTrackIdentity;
        }

        if (track == null || identity == null)
        {
            return;
        }

        OverlayFavoriteVisualState state = await _stateService.ReadAsync(
            track,
            forceRefresh,
            CancellationToken.None);
        if (state == OverlayFavoriteVisualState.Unavailable)
        {
            return;
        }

        CloudMusicFavoriteStateChangedEventArgs? eventArgs = null;
        lock (_gate)
        {
            if (_disposed ||
                !string.Equals(identity, _currentTrackIdentity, StringComparison.Ordinal))
            {
                return;
            }

            if (_lastState is { } previousState && previousState != state)
            {
                eventArgs = new CloudMusicFavoriteStateChangedEventArgs(
                    track,
                    previousState,
                    state);
            }

            _lastState = state;
        }

        if (eventArgs != null)
        {
            StateChanged?.Invoke(this, eventArgs);
        }
    }
}
