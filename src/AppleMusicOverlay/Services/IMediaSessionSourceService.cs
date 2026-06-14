namespace AppleMusicOverlay.Services;

public interface IMediaSessionSourceService
{
    string PreferredSourceAppUserModelId { get; set; }

    Task<IReadOnlyList<MediaSessionCandidate>> ListSessionsAsync(CancellationToken cancellationToken = default);
}
