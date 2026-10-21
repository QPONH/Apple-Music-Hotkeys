using Windows.Media.Control;

namespace AppleMusicOverlay.Services;

public sealed record PlaybackSnapshot(string Title, string Artist, string Album, TimeSpan Position, TimeSpan Duration, bool IsPlaying);

/// <summary>
/// Apple Music playback controls through Windows SMTC.
/// Deliberately Apple Music only: falling back to "whatever session is current" made the app show
/// lyrics of unrelated players (browsers, video apps) as soon as Apple Music was closed.
/// </summary>
public sealed class SmtcMediaSessionService
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;

    private async Task<GlobalSystemMediaTransportControlsSessionManager> GetManagerAsync(CancellationToken cancellationToken)
    {
        if (_manager is not null) return _manager;
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(cancellationToken);
        return _manager;
    }

    /// <summary>Returns the Apple Music session, or null when Apple Music is not running / idle.</summary>
    private async Task<GlobalSystemMediaTransportControlsSession?> GetAppleMusicSessionAsync(CancellationToken cancellationToken = default)
    {
        GlobalSystemMediaTransportControlsSessionManager manager = await GetManagerAsync(cancellationToken);
        var sessions = manager.GetSessions();
        var current = manager.GetCurrentSession();

        if (current is not null && IsAppleMusic(current)) return current;
        return sessions.FirstOrDefault(IsAppleMusic);
    }

    public async Task PreviousAsync(CancellationToken cancellationToken = default)
    {
        var session = await GetAppleMusicSessionAsync(cancellationToken);
        if (session != null) await session.TrySkipPreviousAsync().AsTask(cancellationToken);
    }

    public async Task NextAsync(CancellationToken cancellationToken = default)
    {
        var session = await GetAppleMusicSessionAsync(cancellationToken);
        if (session != null) await session.TrySkipNextAsync().AsTask(cancellationToken);
    }

    public async Task TogglePlayPauseAsync(CancellationToken cancellationToken = default)
    {
        var session = await GetAppleMusicSessionAsync(cancellationToken);
        if (session != null) await session.TryTogglePlayPauseAsync().AsTask(cancellationToken);
    }

    public async Task<(string Title, string Artist)?> GetCurrentTrackAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var session = await GetAppleMusicSessionAsync(cancellationToken);
            if (session is null) return null;

            var properties = await session.TryGetMediaPropertiesAsync().AsTask(cancellationToken);
            if (properties is null || string.IsNullOrWhiteSpace(properties.Title)) return null;
            return (properties.Title, ResolveArtist(properties));
        }
        catch (Exception ex) when (IsSessionGone(ex))
        {
            _manager = null;
            return null;
        }
    }

    public async Task<PlaybackSnapshot?> GetCurrentPlaybackAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var session = await GetAppleMusicSessionAsync(cancellationToken);
            if (session is null) return null;

            // Windows keeps a session listed for a moment after the player closes; its status turns
            // "Closed" first and the metadata becomes stale. Reporting it would show the last song
            // of an app that is no longer running.
            var info = session.GetPlaybackInfo();
            if (info?.PlaybackStatus is GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed) return null;

            var properties = await session.TryGetMediaPropertiesAsync().AsTask(cancellationToken);
            if (properties is null || string.IsNullOrWhiteSpace(properties.Title)) return null;

            var timeline = session.GetTimelineProperties();
            bool playing = info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            double rate = info?.PlaybackRate is double value && value > 0.05 && value < 4 ? value : 1.0;
            TimeSpan duration = ReadDuration(timeline);
            TimeSpan position = AdvancePosition(
                $"{properties.Title}\n{ResolveArtist(properties)}",
                timeline?.Position ?? TimeSpan.Zero,
                playing,
                rate,
                duration);
            return new PlaybackSnapshot(
                properties.Title.Trim(),
                ResolveArtist(properties).Trim(),
                (properties.AlbumTitle ?? string.Empty).Trim(),
                position,
                duration,
                playing);
        }
        catch (Exception ex) when (IsSessionGone(ex))
        {
            // Apple Music was closed or restarted between enumeration and property access.
            // The SMTC manager itself also goes stale after these RPC failures; drop it so the
            // next poll creates a fresh one instead of failing until the process restarts.
            _manager = null;
            return null;
        }
    }

    private string _positionTrackKey = string.Empty;
    private TimeSpan _positionAnchor;
    private DateTime _positionAnchorUtc;

    /// <summary>
    /// Advances the reported position to "now". SMTC only refreshes <c>TimelineProperties.Position</c>
    /// about once per second, and <c>LastUpdatedTime</c> is not a reliable clock (for Apple Music it
    /// can lag far behind the reported position). Trusting it made the overlay jump ahead of the
    /// audio, so the elapsed time since our own previous poll is used instead: the position runs at
    /// real time and snaps to the reported value when that value is ahead (seek forward) or more
    /// than two seconds behind (seek back). Pausing freezes it.
    /// </summary>
    private TimeSpan AdvancePosition(string trackKey, TimeSpan reported, bool playing, double rate, TimeSpan duration)
    {
        DateTime now = DateTime.UtcNow;
        TimeSpan position;

        if (!playing || !string.Equals(trackKey, _positionTrackKey, StringComparison.Ordinal))
        {
            position = reported;
            _positionTrackKey = trackKey;
            _positionAnchor = position;
            _positionAnchorUtc = now;
        }
        else
        {
            double elapsed = (now - _positionAnchorUtc).TotalSeconds;
            if (elapsed < 0) elapsed = 0;
            TimeSpan predicted = _positionAnchor + TimeSpan.FromSeconds(elapsed * rate);

            if (reported > predicted + TimeSpan.FromMilliseconds(400)) position = reported;   // sample is ahead
            else if (reported < predicted - TimeSpan.FromSeconds(2)) position = reported;     // real seek back
            else position = predicted;

            _positionAnchor = position;
            _positionAnchorUtc = now;
        }

        if (position < TimeSpan.Zero) position = TimeSpan.Zero;
        if (duration > TimeSpan.Zero && position > duration) position = duration;
        return position;
    }

    /// <summary>
    /// Track length for lyric matching. Apple Music occasionally reports an absolute or empty
    /// end time; using that raw value made every real lyric miss the duration check.
    /// </summary>
    private static TimeSpan ReadDuration(GlobalSystemMediaTransportControlsSessionTimelineProperties? timeline)
    {
        if (timeline is null) return TimeSpan.Zero;

        TimeSpan duration = TimeSpan.Zero;
        if (timeline.EndTime > timeline.StartTime)
            duration = timeline.EndTime - timeline.StartTime;
        if ((duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(30)) && timeline.MaxSeekTime > timeline.MinSeekTime)
            duration = timeline.MaxSeekTime - timeline.MinSeekTime;

        // Unknown is safer than a bogus length: callers treat zero as "do not filter on duration".
        if (duration < TimeSpan.FromSeconds(1) || duration > TimeSpan.FromMinutes(30)) return TimeSpan.Zero;
        return duration;
    }

    /// <summary>Exceptions SMTC throws when the player owning a session disappears.</summary>
    private static bool IsSessionGone(Exception ex) =>
        ex is System.Runtime.InteropServices.COMException or ObjectDisposedException or TimeoutException;

    /// <summary>
    /// Some tracks report an empty Artist but a filled AlbumArtist; using it instead of an empty
    /// artist keeps lyric lookups working for those albums.
    /// </summary>
    private static string ResolveArtist(GlobalSystemMediaTransportControlsSessionMediaProperties properties)
    {
        if (!string.IsNullOrWhiteSpace(properties.Artist)) return properties.Artist;
        return properties.AlbumArtist ?? string.Empty;
    }

    public async Task<bool> IsAppleMusicPlayingAsync(CancellationToken cancellationToken = default)
    {
        var manager = await GetManagerAsync(cancellationToken);
        foreach (var session in manager.GetSessions())
        {
            try
            {
                if (!IsAppleMusic(session)) continue;
                var info = session.GetPlaybackInfo();
                if (info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) return true;
            }
            catch (Exception ex) when (IsSessionGone(ex))
            {
                // Session vanished mid-enumeration; ignore it.
            }
        }
        return false;
    }

    private static bool IsAppleMusic(GlobalSystemMediaTransportControlsSession session)
    {
        string source = session.SourceAppUserModelId ?? string.Empty;
        // Apple Music registers identities such as "AppleInc.AppleMusicWin_...!App" (Store) or a
        // plain "AppleMusic" one. A broad "Apple" match would also catch iTunes and Apple TV,
        // which is exactly how unrelated lyrics used to appear.
        return source.Contains("AppleMusic", StringComparison.OrdinalIgnoreCase)
            || source.Contains("Apple Music", StringComparison.OrdinalIgnoreCase)
            || source.Contains("Apple.Music", StringComparison.OrdinalIgnoreCase);
    }
}
