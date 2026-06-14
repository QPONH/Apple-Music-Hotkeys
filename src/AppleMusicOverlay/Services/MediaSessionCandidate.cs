namespace AppleMusicOverlay.Services;

public sealed record MediaSessionCandidate(
    string SourceAppUserModelId,
    string Title,
    string Artist,
    bool IsPlaying,
    bool IsCurrent,
    int Index);
