using Windows.Media.Control;

namespace AppleMusicOverlay.Services;

public sealed record PlaybackSnapshot(string Title, string Artist, TimeSpan Position, TimeSpan Duration);

/// <summary>
/// Apple Music playback controls through Windows SMTC.
/// Deliberately Apple Music only: falling back to "whatever session is current" made the app show
/// lyrics of unrelated players (browsers, video apps) as soon as Apple Music was closed.
/// </summary>
public sealed class SmtcMediaSessionService
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;

    private async Task<GlobalSystemMediaTransportControlsSessionManager> GetManagerAsync(CancellationToken cancellationToken) =>
        _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(cancellationToken);

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
            return new PlaybackSnapshot(
                properties.Title,
                ResolveArtist(properties),
                timeline?.Position ?? TimeSpan.Zero,
                timeline?.EndTime ?? TimeSpan.Zero);
        }
        catch (Exception ex) when (IsSessionGone(ex))
        {
            // Apple Music was closed or restarted between enumeration and property access.
            // This is polled every 350 ms, so it must stay silent instead of filling the crash
            // log with thousands of "RPC server unavailable" entries.
            return null;
        }
    }

    /// <summary>Exceptions SMTC throws when the player owning a session disappears.</summary>
    private static bool IsSessionGone(Exception ex) =>
        ex is System.Runtime.InteropServices.COMException or ObjectDisposedException;

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
