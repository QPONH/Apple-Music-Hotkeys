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

    public Task PreviousAsync(CancellationToken cancellationToken = default) => SkipAsync(next: false, cancellationToken);

    public Task NextAsync(CancellationToken cancellationToken = default) => SkipAsync(next: true, cancellationToken);

    /// <summary>
    /// Sends a skip command and verifies that the playing track actually changed. The first skip
    /// after Apple Music starts a new item can be accepted by SMTC but executed by the player as a
    /// restart of the current song (its queue is not ready yet), which is why the first hot key
    /// press used to do nothing and the second one worked. At most two commands are sent.
    /// </summary>
    private async Task SkipAsync(bool next, CancellationToken cancellationToken)
    {
        string? before = await ReadTrackKeyAsync(cancellationToken);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var session = await GetAppleMusicSessionAsync(cancellationToken);
                if (session is null)
                {
                    _manager = null;
                    await Task.Delay(TimeSpan.FromMilliseconds(140), cancellationToken);
                    continue;
                }

                bool accepted = next
                    ? await session.TrySkipNextAsync().AsTask(cancellationToken)
                    : await session.TrySkipPreviousAsync().AsTask(cancellationToken);
                if (accepted && await WaitForTrackChangeAsync(before, cancellationToken)) return;
            }
            catch (Exception ex) when (IsSessionGone(ex))
            {
                // The session or its manager went stale mid-call; rebuild before retrying.
            }

            _manager = null;
            await Task.Delay(TimeSpan.FromMilliseconds(160), cancellationToken);
        }
    }

    private async Task<string?> ReadTrackKeyAsync(CancellationToken cancellationToken)
    {
        try
        {
            var session = await GetAppleMusicSessionAsync(cancellationToken);
            if (session is null) return null;
            var properties = await session.TryGetMediaPropertiesAsync().AsTask(cancellationToken);
            if (properties is null) return null;
            return properties.Title + "\n" + ResolveArtist(properties);
        }
        catch (Exception ex) when (IsSessionGone(ex))
        {
            _manager = null;
            return null;
        }
    }

    private async Task<bool> WaitForTrackChangeAsync(string? before, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(before)) return true;
        for (int poll = 0; poll < 5; poll++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(80), cancellationToken);
            string? current = await ReadTrackKeyAsync(cancellationToken);
            if (!string.IsNullOrEmpty(current) && !string.Equals(current, before, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    public async Task TogglePlayPauseAsync(CancellationToken cancellationToken = default)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var session = await GetAppleMusicSessionAsync(cancellationToken);
                if (session is not null && await session.TryTogglePlayPauseAsync().AsTask(cancellationToken)) return;
            }
            catch (Exception ex) when (IsSessionGone(ex))
            {
                // Retry once with a fresh manager.
            }

            _manager = null;
            await Task.Delay(TimeSpan.FromMilliseconds(140), cancellationToken);
        }
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

            // TryGetMediaPropertiesAsync crosses into the player process and is by far the most
            // expensive call on this path — it runs on every overlay tick (~8 Hz) only to read
            // title/artist/album, which almost never change. It is cached for half a second, which
            // keeps track-change detection prompt while removing most of the interop traffic.
            DateTime nowUtc = DateTime.UtcNow;
            if (_cachedProperties is null
                || !string.Equals(_cachedPropertiesSessionId, session.SourceAppUserModelId, StringComparison.Ordinal)
                || nowUtc - _cachedPropertiesUtc > MetadataTtl)
            {
                _cachedProperties = await session.TryGetMediaPropertiesAsync().AsTask(cancellationToken);
                _cachedPropertiesUtc = nowUtc;
                _cachedPropertiesSessionId = session.SourceAppUserModelId;
            }

            var properties = _cachedProperties;
            if (properties is null || string.IsNullOrWhiteSpace(properties.Title)) return null;

            var timeline = session.GetTimelineProperties();
            bool playing = info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            double rate = info?.PlaybackRate is double value && value > 0.05 && value < 4 ? value : 1.0;
            TimeSpan duration = ReadDuration(timeline);
            TimeSpan reportedPosition = timeline?.Position ?? TimeSpan.Zero;

            // Apple Music sometimes reports an end time that is an absolute clock rather than the
            // track length, which yields a "duration" shorter than the position already played.
            // The position is clamped to the duration, so trusting such a value freezes the overlay
            // mid-song; fall back to a length derived from the position instead of dropping it,
            // because the lyric lookup still needs a usable duration for version matching.
            if (duration > TimeSpan.Zero && duration < reportedPosition + TimeSpan.FromSeconds(5))
                duration = reportedPosition + TimeSpan.FromSeconds(1);

            // This runs on every overlay tick (~8 Hz), so the expensive parts (artist resolution and
            // the title/artist concatenation used as the track key) are only rebuilt when the track
            // actually changes.
            string title = properties.Title;
            string artist = ResolveArtist(properties);
            if (!string.Equals(title, _cachedTitle, StringComparison.Ordinal)
                || !string.Equals(artist, _cachedArtist, StringComparison.Ordinal))
            {
                _cachedTitle = title;
                _cachedArtist = artist;
                _cachedTrackKey = title + "\n" + artist;
                _cachedAlbum = (properties.AlbumTitle ?? string.Empty).Trim();
            }

            TimeSpan position = AdvancePosition(_cachedTrackKey, reportedPosition, playing, rate, duration);
            return new PlaybackSnapshot(
                title.Trim(),
                artist.Trim(),
                _cachedAlbum,
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
            // The cached metadata belonged to the session that just died; a fresh one must be read.
            _cachedProperties = null;
            _cachedPropertiesSessionId = string.Empty;
            return null;
        }
    }

    private string _positionTrackKey = string.Empty;
    private GlobalSystemMediaTransportControlsSessionMediaProperties? _cachedProperties;
    private DateTime _cachedPropertiesUtc;
    private string _cachedPropertiesSessionId = string.Empty;
    private static readonly TimeSpan MetadataTtl = TimeSpan.FromMilliseconds(500);
    private string _cachedTitle = string.Empty;
    private string _cachedArtist = string.Empty;
    private string _cachedAlbum = string.Empty;
    private string _cachedTrackKey = string.Empty;
    private TimeSpan _anchorPosition;
    private DateTime _anchorUtc;
    private TimeSpan _lastReported = TimeSpan.MinValue;
    private TimeSpan _position;
    private DateTime _lastTickUtc;
    private bool _positionSeeded;
    private bool _wasPlaying;

    /// <summary>
    /// Apple Music's media-session position trails the audio by about a second: with an offset of
    /// −1000 ms the overlay finally matched the music. A negative offset moves the lyric evaluation
    /// forward in time, so the compensation adds this much to the position — measured on the
    /// reference machine, and kept here so the slider can stay at zero.
    /// </summary>
    private static readonly TimeSpan SessionLag = TimeSpan.FromMilliseconds(1000);

    /// <summary>
    /// Turns the once-per-second session position into a smooth, strictly increasing clock.
    ///
    /// Each fresh sample starts a new anchor (sample time + elapsed since the sample), which makes the
    /// target advance at real time; the returned position follows that target with a low-pass filter
    /// and is clamped so it can never step backwards. The previous implementation re-based on either
    /// the sample or its own prediction every poll — that is what made a single song speed up and
    /// slow down.
    /// </summary>
    private TimeSpan AdvancePosition(string trackKey, TimeSpan reported, bool playing, double rate, TimeSpan duration)
    {
        DateTime now = DateTime.UtcNow;

        if (!string.Equals(trackKey, _positionTrackKey, StringComparison.Ordinal))
        {
            _positionTrackKey = trackKey;
            _positionSeeded = false;
            _lastReported = TimeSpan.MinValue;
        }

        // The session value is later than the audio, so the lyric lane is shifted forward by the lag.
        TimeSpan compensated = reported + SessionLag;

        if (!playing)
        {
            // Paused: freeze instead of re-seeding from a stale sample.
            _wasPlaying = false;
            return Clamp(_positionSeeded ? _position : compensated, duration);
        }

        // Resuming must re-anchor: otherwise the whole paused stretch is added as elapsed time and
        // the overlay jumps forward by the pause length.
        if (!_wasPlaying) _positionSeeded = false;
        _wasPlaying = true;

        if (!_positionSeeded || reported != _lastReported)
        {
            _lastReported = reported;
            _anchorPosition = compensated;
            _anchorUtc = now;
        }

        TimeSpan target = _anchorPosition + (now - _anchorUtc);
        if (!_positionSeeded)
        {
            _position = target;
            _positionSeeded = true;
            _lastTickUtc = now;
            return Clamp(_position, duration);
        }

        // Advance by the real elapsed time first, then apply a small share of the remaining error.
        // A pure "follow the target" filter runs a constant distance behind it (the error can never
        // close while the target keeps moving), which is exactly the fixed lag this is meant to fix.
        TimeSpan sinceLastTick = now - _lastTickUtc;
        if (sinceLastTick < TimeSpan.Zero) sinceLastTick = TimeSpan.Zero;
        _lastTickUtc = now;

        TimeSpan next = _position + sinceLastTick;
        TimeSpan error = target - next;
        if (Math.Abs(error.TotalSeconds) > 3)
            next = target;                                   // seek or large correction: jump
        else
            next += TimeSpan.FromMilliseconds(error.TotalMilliseconds * 0.08);

        // Hold the clock steady against sampling jitter, but do not clamp it hard: always forcing it
        // forward turns every jitter swing into a permanent ratchet, which ended up overshooting the
        // compensated lane by several hundred milliseconds.
        if (next < _position - TimeSpan.FromMilliseconds(250)) next = _position - TimeSpan.FromMilliseconds(250);

        _position = Clamp(next, duration);
        LastReportedPosition = reported;
        LastComputedPosition = _position;
        return _position;
    }

    /// <summary>Raw session position from the last poll, for the sync diagnostic line.</summary>
    public static TimeSpan LastReportedPosition { get; private set; }

    /// <summary>Compensated position handed to the overlay on the last poll.</summary>
    public static TimeSpan LastComputedPosition { get; private set; }

    private static TimeSpan Clamp(TimeSpan position, TimeSpan duration)
    {
        if (position < TimeSpan.Zero) return TimeSpan.Zero;
        if (duration > TimeSpan.Zero && position > duration) return duration;
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
