using AppleMusicOverlay.Models;

namespace AppleMusicOverlay.Services;

public static class TrackIdentity
{
    public static string Create(TrackInfo track)
    {
        return string.Join('\u001f', Normalize(track.SourceAppId), Normalize(track.Title), Normalize(track.Artist));
    }

    private static string Normalize(string value)
    {
        return value.Trim().ToUpperInvariant();
    }
}
