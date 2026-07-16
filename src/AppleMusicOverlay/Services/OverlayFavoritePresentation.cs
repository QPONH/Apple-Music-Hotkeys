using System.Globalization;
using System.Text;
using AppleMusicOverlay.Models;

namespace AppleMusicOverlay.Services;

public enum OverlayFavoriteVisualState
{
    Unavailable = 0,
    NotFavorite = 1,
    Favorite = 2
}

public readonly record struct OverlayTrackContentLayout(
    double TextLeft,
    double TextWidth,
    double TitleTop,
    double ArtistTop,
    bool CenterText,
    bool ShowFavoriteStar,
    double StarLeft,
    double StarTop,
    double StarSize);

public sealed record OverlayFavoriteResolution(
    TrackInfo Track,
    OverlayFavoriteVisualState State);

public sealed class OverlayFavoriteStatusCoordinator : IDisposable
{
    private readonly Func<CancellationToken, Task<AppleMusicFavoriteStatus>> _readStatusAsync;
    private readonly TimeSpan _readTimeout;
    private readonly TimeSpan _retryDelay;
    private readonly int _maxAttempts;
    private readonly object _gate = new();
    private CancellationTokenSource? _activeRequestCts;
    private long _revision;

    public OverlayFavoriteStatusCoordinator(
        Func<CancellationToken, Task<AppleMusicFavoriteStatus>> readStatusAsync,
        TimeSpan? readTimeout = null,
        TimeSpan? retryDelay = null,
        int maxAttempts = 3)
    {
        _readStatusAsync = readStatusAsync;
        _readTimeout = readTimeout ?? TimeSpan.FromMilliseconds(900);
        _retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(140);
        _maxAttempts = Math.Max(1, maxAttempts);
    }

    public async Task<OverlayFavoriteResolution?> ResolveAsync(TrackInfo track)
    {
        long revision;
        CancellationTokenSource requestCts;
        lock (_gate)
        {
            revision = ++_revision;
            _activeRequestCts?.Cancel();
            _activeRequestCts?.Dispose();
            requestCts = new CancellationTokenSource();
            requestCts.CancelAfter(_readTimeout);
            _activeRequestCts = requestCts;
        }

        if (!OverlayFavoritePresentation.IsSupportedAppleMusicSource(track.SourceAppId))
        {
            return IsCurrent(revision)
                ? new OverlayFavoriteResolution(track, OverlayFavoriteVisualState.Unavailable)
                : null;
        }

        try
        {
            for (int attempt = 0; attempt < _maxAttempts; attempt++)
            {
                AppleMusicFavoriteStatus status = await _readStatusAsync(requestCts.Token);
                if (!IsCurrent(revision))
                {
                    return null;
                }

                OverlayFavoriteVisualState state = OverlayFavoritePresentation.Resolve(track, status);
                if (state != OverlayFavoriteVisualState.Unavailable || attempt == _maxAttempts - 1)
                {
                    return new OverlayFavoriteResolution(track, state);
                }

                await Task.Delay(_retryDelay, requestCts.Token);
            }

            return new OverlayFavoriteResolution(track, OverlayFavoriteVisualState.Unavailable);
        }
        catch (OperationCanceledException) when (!IsCurrent(revision))
        {
            return null;
        }
        catch
        {
            return IsCurrent(revision)
                ? new OverlayFavoriteResolution(track, OverlayFavoriteVisualState.Unavailable)
                : null;
        }
    }

    public void Dispose()
    {
        CancelPending();
    }

    public void CancelPending()
    {
        lock (_gate)
        {
            _revision++;
            _activeRequestCts?.Cancel();
            _activeRequestCts?.Dispose();
            _activeRequestCts = null;
        }
    }

    private bool IsCurrent(long revision)
    {
        lock (_gate)
        {
            return revision == _revision;
        }
    }
}

public static class OverlayFavoritePresentation
{
    private const double SfProArtistOpticalOffset = 2;

    public static bool IsSupportedAppleMusicSource(string? sourceAppId)
    {
        if (string.IsNullOrWhiteSpace(sourceAppId))
        {
            return false;
        }

        return sourceAppId.Contains("AppleMusic", StringComparison.OrdinalIgnoreCase) ||
               sourceAppId.Contains("AppleInc.AppleMusicWin", StringComparison.OrdinalIgnoreCase) ||
               sourceAppId.Contains("music.apple.com", StringComparison.OrdinalIgnoreCase);
    }

    public static OverlayFavoriteVisualState Resolve(TrackInfo track, AppleMusicFavoriteStatus status)
    {
        if (!IsSupportedAppleMusicSource(track.SourceAppId) ||
            status.State == FavoriteButtonState.Unknown ||
            !MatchesTrack(track, status.TrackIdentity))
        {
            return OverlayFavoriteVisualState.Unavailable;
        }

        return status.State == FavoriteButtonState.AlreadyFavorite
            ? OverlayFavoriteVisualState.Favorite
            : OverlayFavoriteVisualState.NotFavorite;
    }

    public static OverlayTrackContentLayout CreateLayout(
        OverlayFavoriteVisualState favoriteState,
        bool showTitle,
        bool showArtist)
    {
        if (favoriteState == OverlayFavoriteVisualState.Unavailable)
        {
            return new OverlayTrackContentLayout(
                4,
                248,
                228,
                249,
                CenterText: true,
                ShowFavoriteStar: false,
                StarLeft: 0,
                StarTop: 0,
                StarSize: 0);
        }

        double titleTop = showTitle && !showArtist ? 238 : 228;
        double artistTop = showArtist && !showTitle ? 239 : 249;
        return new OverlayTrackContentLayout(
            40,
            140,
            titleTop,
            artistTop,
            CenterText: false,
            ShowFavoriteStar: true,
            StarLeft: 194,
            StarTop: 236,
            StarSize: 22);
    }

    public static double GetArtistOpticalOffset(string? fontId, bool centerText)
    {
        return !centerText &&
               OverlayTrackFontIds.NormalizeKnownId(fontId) == OverlayTrackFontIds.SfPro
            ? SfProArtistOpticalOffset
            : 0;
    }

    private static bool MatchesTrack(TrackInfo track, string? automationIdentity)
    {
        if (string.IsNullOrWhiteSpace(automationIdentity) ||
            string.IsNullOrWhiteSpace(track.Title) ||
            string.IsNullOrWhiteSpace(track.Artist))
        {
            return false;
        }

        string identity = Normalize(automationIdentity);
        return identity.Contains(Normalize(track.Title), StringComparison.Ordinal) &&
               identity.Contains(Normalize(track.Artist), StringComparison.Ordinal);
    }

    private static string Normalize(string value)
    {
        string normalized = value.Normalize(NormalizationForm.FormKC).Trim().ToUpperInvariant();
        var builder = new StringBuilder(normalized.Length);
        bool previousWasWhitespace = false;
        foreach (char character in normalized)
        {
            bool isWhitespace = char.GetUnicodeCategory(character) is UnicodeCategory.SpaceSeparator or
                UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator || char.IsWhiteSpace(character);
            if (isWhitespace)
            {
                if (!previousWasWhitespace)
                {
                    builder.Append(' ');
                }

                previousWasWhitespace = true;
                continue;
            }

            builder.Append(character);
            previousWasWhitespace = false;
        }

        return builder.ToString();
    }
}
