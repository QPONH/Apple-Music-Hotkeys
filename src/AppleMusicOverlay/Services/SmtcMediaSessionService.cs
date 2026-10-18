using Windows.Media.Control;

namespace AppleMusicOverlay.Services;

public sealed record PlaybackSnapshot(string Title, string Artist, TimeSpan Position, TimeSpan Duration);

/// <summary>Apple Music playback controls through Windows SMTC.</summary>
public sealed class SmtcMediaSessionService
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;

    private async Task<GlobalSystemMediaTransportControlsSession?> GetAppleMusicSessionAsync(CancellationToken cancellationToken = default)
    {
        _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(cancellationToken);
        var sessions = _manager.GetSessions();
        var current = _manager.GetCurrentSession();

        if (current != null && IsAppleMusic(current)) return current;
        return sessions.FirstOrDefault(IsAppleMusic)
            ?? current
            ?? sessions.FirstOrDefault();
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
        _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(cancellationToken);
        var sessions = _manager.GetSessions();
        var preferred = sessions.FirstOrDefault(IsAppleMusic) ?? _manager.GetCurrentSession() ?? sessions.FirstOrDefault();
        foreach (var session in (preferred is null ? sessions : new[] { preferred }.Concat(sessions.Where(s => s != preferred))))
        {
            var properties = await session.TryGetMediaPropertiesAsync().AsTask(cancellationToken);
            if (properties is not null && !string.IsNullOrWhiteSpace(properties.Title))
                return (properties.Title, properties.Artist ?? string.Empty);
        }
        return null;
    }

    public async Task<PlaybackSnapshot?> GetCurrentPlaybackAsync(CancellationToken cancellationToken = default)
    {
        var session = await GetAppleMusicSessionAsync(cancellationToken);
        if (session is null) return null;
        var properties = await session.TryGetMediaPropertiesAsync().AsTask(cancellationToken);
        if (properties is null || string.IsNullOrWhiteSpace(properties.Title)) return null;
        var timeline = session.GetTimelineProperties();
        return new PlaybackSnapshot(properties.Title, properties.Artist ?? string.Empty, timeline?.Position ?? TimeSpan.Zero, timeline?.EndTime ?? TimeSpan.Zero);
    }

    public async Task<bool> IsAppleMusicPlayingAsync(CancellationToken cancellationToken = default)
    {
        _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(cancellationToken);
        foreach (var session in _manager.GetSessions())
        {
            if (!IsAppleMusic(session)) continue;
            var info = session.GetPlaybackInfo();
            if (info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) return true;
        }
        return false;
    }

    private static bool IsAppleMusic(GlobalSystemMediaTransportControlsSession session)
    {
        string source = session.SourceAppUserModelId ?? string.Empty;
        return source.Contains("AppleMusic", StringComparison.OrdinalIgnoreCase)
            || source.Contains("Apple.Music", StringComparison.OrdinalIgnoreCase)
            || source.Contains("AppleInc", StringComparison.OrdinalIgnoreCase)
            || source.Contains("Apple", StringComparison.OrdinalIgnoreCase);
    }
}
