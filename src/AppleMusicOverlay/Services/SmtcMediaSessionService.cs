using Windows.Media.Control;

namespace AppleMusicOverlay.Services;

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
        return sessions.FirstOrDefault(IsAppleMusic) ?? current;
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

    private static bool IsAppleMusic(GlobalSystemMediaTransportControlsSession session)
    {
        string source = session.SourceAppUserModelId ?? string.Empty;
        return source.Contains("AppleMusic", StringComparison.OrdinalIgnoreCase)
            || source.Contains("Apple.Music", StringComparison.OrdinalIgnoreCase)
            || source.Contains("AppleInc", StringComparison.OrdinalIgnoreCase);
    }
}
