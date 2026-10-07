namespace AppleMusicOverlay.Models;

public sealed record TrackInfo(
    string Title,
    string Artist,
    byte[]? CoverBytes,
    string SourceAppId,
    TimeSpan Duration,
    bool IsPlaying);
