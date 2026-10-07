namespace AppleMusicOverlay.Services;

public static class AppleMusicSmtcMetadataNormalizer
{
    private const string WindowsAppSourcePrefix = "AppleInc.AppleMusicWin_";
    private const string WindowsAppSourceSuffix = "!App";
    private const string FallbackAlbumSeparator = " \u2014 ";
    private static readonly string[] AlbumSeparators = [" \u2014 ", " \u2013 ", " - "];

    public static string NormalizeArtist(
        string sourceAppUserModelId,
        string artist,
        string? albumTitle)
    {
        if (!IsAppleMusicWindowsApp(sourceAppUserModelId))
        {
            return artist;
        }

        if (string.IsNullOrWhiteSpace(albumTitle))
        {
            int separatorIndex = artist.IndexOf(FallbackAlbumSeparator, StringComparison.Ordinal);
            if (separatorIndex <= 0 || separatorIndex + FallbackAlbumSeparator.Length >= artist.Length)
            {
                return artist;
            }

            string artistWithoutAlbum = artist[..separatorIndex].TrimEnd();
            return string.IsNullOrWhiteSpace(artistWithoutAlbum) ? artist : artistWithoutAlbum;
        }

        string album = albumTitle.Trim();
        foreach (string separator in AlbumSeparators)
        {
            string suffix = separator + album;
            if (!artist.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string normalizedArtist = artist[..^suffix.Length].TrimEnd();
            return string.IsNullOrWhiteSpace(normalizedArtist) ? artist : normalizedArtist;
        }

        return artist;
    }

    private static bool IsAppleMusicWindowsApp(string sourceAppUserModelId)
    {
        return sourceAppUserModelId.StartsWith(WindowsAppSourcePrefix, StringComparison.OrdinalIgnoreCase) &&
               sourceAppUserModelId.EndsWith(WindowsAppSourceSuffix, StringComparison.OrdinalIgnoreCase);
    }
}
