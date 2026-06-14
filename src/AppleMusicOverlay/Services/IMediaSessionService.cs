using AppleMusicOverlay.Models;

namespace AppleMusicOverlay.Services;

public interface IMediaSessionService
{
    Task<TrackInfo?> GetCurrentTrackAsync(CancellationToken cancellationToken = default);

    Task PreviousAsync(CancellationToken cancellationToken = default);

    Task NextAsync(CancellationToken cancellationToken = default);

    Task TogglePlayPauseAsync(CancellationToken cancellationToken = default);
}
