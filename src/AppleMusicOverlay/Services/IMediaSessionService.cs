using AppleMusicOverlay.Models;

namespace AppleMusicOverlay.Services;

public readonly record struct MediaSessionReadOptions(bool IncludeCover)
{
    public static MediaSessionReadOptions Full { get; } = new(true);

    public static MediaSessionReadOptions MetadataOnly { get; } = new(false);
}

public interface IMediaSessionChangeNotifier
{
    event EventHandler? MediaSessionChanged;
}

public interface IMediaSessionService
{
    Task<TrackInfo?> GetCurrentTrackAsync(CancellationToken cancellationToken = default);

    Task<TrackInfo?> GetCurrentTrackAsync(MediaSessionReadOptions options, CancellationToken cancellationToken = default)
    {
        return GetCurrentTrackAsync(cancellationToken);
    }

    Task PreviousAsync(CancellationToken cancellationToken = default);

    Task NextAsync(CancellationToken cancellationToken = default);

    Task TogglePlayPauseAsync(CancellationToken cancellationToken = default);
}
