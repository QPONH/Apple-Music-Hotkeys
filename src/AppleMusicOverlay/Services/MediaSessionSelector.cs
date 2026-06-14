namespace AppleMusicOverlay.Services;

public static class MediaSessionSelector
{
    public static MediaSessionCandidate? SelectBest(
        IReadOnlyCollection<MediaSessionCandidate> candidates,
        string? preferredSourceAppUserModelId = null)
    {
        if (!string.IsNullOrWhiteSpace(preferredSourceAppUserModelId))
        {
            MediaSessionCandidate? preferred = candidates.FirstOrDefault(candidate =>
                candidate.SourceAppUserModelId.Equals(preferredSourceAppUserModelId, StringComparison.OrdinalIgnoreCase));
            if (preferred != null)
            {
                return preferred;
            }
        }

        return candidates
            .OrderByDescending(Score)
            .ThenBy(candidate => candidate.Index)
            .FirstOrDefault();
    }

    private static int Score(MediaSessionCandidate candidate)
    {
        int score = 0;
        string source = candidate.SourceAppUserModelId;
        string title = candidate.Title;
        string artist = candidate.Artist;

        if (ContainsAny(source, "applemusic", "apple music"))
        {
            score += 300;
        }

        if (ContainsAny(source, "msedge", "microsoftedge", "microsoft.microsoftedge"))
        {
            score += 220;
        }

        if (ContainsAny(title, "apple music") || ContainsAny(artist, "apple music"))
        {
            score += 160;
        }

        if (candidate.IsPlaying)
        {
            score += 40;
        }

        if (candidate.IsCurrent)
        {
            score += 30;
        }

        if (!string.IsNullOrWhiteSpace(title) && !title.Equals("Unknown Track", StringComparison.OrdinalIgnoreCase))
        {
            score += 10;
        }

        if (!string.IsNullOrWhiteSpace(artist) && !artist.Equals("Unknown Artist", StringComparison.OrdinalIgnoreCase))
        {
            score += 10;
        }

        return score;
    }

    private static bool ContainsAny(string value, params string[] needles)
    {
        foreach (string needle in needles)
        {
            if (value.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
