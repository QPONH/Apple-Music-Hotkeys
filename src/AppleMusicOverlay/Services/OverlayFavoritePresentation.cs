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

public interface IOverlayFavoriteStateReader
{
    bool IsSupportedSource(string? sourceAppId);

    Task<OverlayFavoriteVisualState> ReadAsync(
        TrackInfo track,
        CancellationToken cancellationToken);
}

public sealed class CompositeOverlayFavoriteStateReader(
    params IOverlayFavoriteStateReader[] readers) : IOverlayFavoriteStateReader
{
    private readonly IOverlayFavoriteStateReader[] _readers = readers;

    public bool IsSupportedSource(string? sourceAppId)
    {
        return _readers.Any(reader => reader.IsSupportedSource(sourceAppId));
    }

    public Task<OverlayFavoriteVisualState> ReadAsync(
        TrackInfo track,
        CancellationToken cancellationToken)
    {
        IOverlayFavoriteStateReader? reader = _readers.FirstOrDefault(
            candidate => candidate.IsSupportedSource(track.SourceAppId));
        return reader == null
            ? Task.FromResult(OverlayFavoriteVisualState.Unavailable)
            : reader.ReadAsync(track, cancellationToken);
    }
}

public sealed class AppleMusicOverlayFavoriteStateReader(
    Func<CancellationToken, Task<AppleMusicFavoriteStatus>> readStatusAsync)
    : IOverlayFavoriteStateReader
{
    public bool IsSupportedSource(string? sourceAppId)
    {
        return OverlayFavoritePresentation.IsSupportedAppleMusicSource(sourceAppId);
    }

    public async Task<OverlayFavoriteVisualState> ReadAsync(
        TrackInfo track,
        CancellationToken cancellationToken)
    {
        AppleMusicFavoriteStatus status = await readStatusAsync(cancellationToken);
        return OverlayFavoritePresentation.Resolve(track, status);
    }
}

public sealed class OverlayFavoriteStatusCoordinator : IDisposable
{
    private static readonly TimeSpan DefaultAppleMusicReadTimeout = TimeSpan.FromMilliseconds(1600);
    private const int DefaultAppleMusicMaxAttempts = 8;
    private readonly IOverlayFavoriteStateReader _stateReader;
    private readonly TimeSpan _readTimeout;
    private readonly TimeSpan _appleMusicReadTimeout;
    private readonly TimeSpan _retryDelay;
    private readonly int _maxAttempts;
    private readonly int _appleMusicMaxAttempts;
    private readonly object _gate = new();
    private CancellationTokenSource? _activeRequestCts;
    private long _revision;

    public OverlayFavoriteStatusCoordinator(
        Func<CancellationToken, Task<AppleMusicFavoriteStatus>> readStatusAsync,
        TimeSpan? readTimeout = null,
        TimeSpan? retryDelay = null,
        int maxAttempts = 3,
        TimeSpan? appleMusicReadTimeout = null,
        int appleMusicMaxAttempts = DefaultAppleMusicMaxAttempts)
        : this(
            new AppleMusicOverlayFavoriteStateReader(readStatusAsync),
            readTimeout,
            retryDelay,
            maxAttempts,
            appleMusicReadTimeout,
            appleMusicMaxAttempts)
    {
    }

    public OverlayFavoriteStatusCoordinator(
        IOverlayFavoriteStateReader stateReader,
        TimeSpan? readTimeout = null,
        TimeSpan? retryDelay = null,
        int maxAttempts = 3,
        TimeSpan? appleMusicReadTimeout = null,
        int appleMusicMaxAttempts = DefaultAppleMusicMaxAttempts)
    {
        _stateReader = stateReader;
        _readTimeout = readTimeout ?? TimeSpan.FromMilliseconds(900);
        _appleMusicReadTimeout = appleMusicReadTimeout ??
                                     readTimeout ??
                                     DefaultAppleMusicReadTimeout;
        _retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(140);
        _maxAttempts = Math.Max(1, maxAttempts);
        _appleMusicMaxAttempts = Math.Max(_maxAttempts, appleMusicMaxAttempts);
    }

    public async Task<OverlayFavoriteResolution?> ResolveAsync(TrackInfo track)
    {
        bool isAppleMusic = OverlayFavoritePresentation.IsSupportedAppleMusicSource(track.SourceAppId);
        TimeSpan readTimeout = isAppleMusic ? _appleMusicReadTimeout : _readTimeout;
        int maxAttempts = isAppleMusic ? _appleMusicMaxAttempts : _maxAttempts;
        long revision;
        CancellationTokenSource requestCts;
        lock (_gate)
        {
            revision = ++_revision;
            _activeRequestCts?.Cancel();
            _activeRequestCts?.Dispose();
            requestCts = new CancellationTokenSource();
            requestCts.CancelAfter(readTimeout);
            _activeRequestCts = requestCts;
        }

        if (!_stateReader.IsSupportedSource(track.SourceAppId))
        {
            return IsCurrent(revision)
                ? new OverlayFavoriteResolution(track, OverlayFavoriteVisualState.Unavailable)
                : null;
        }

        try
        {
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                OverlayFavoriteVisualState state = await _stateReader.ReadAsync(track, requestCts.Token);
                if (!IsCurrent(revision))
                {
                    return null;
                }

                if (state != OverlayFavoriteVisualState.Unavailable || attempt == maxAttempts - 1)
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

    public static bool IsSupportedCloudMusicSource(string? sourceAppId)
    {
        if (string.IsNullOrWhiteSpace(sourceAppId))
        {
            return false;
        }

        return sourceAppId.Equals("cloudmusic.exe", StringComparison.OrdinalIgnoreCase) ||
               sourceAppId.EndsWith("\\cloudmusic.exe", StringComparison.OrdinalIgnoreCase) ||
               sourceAppId.EndsWith("/cloudmusic.exe", StringComparison.OrdinalIgnoreCase);
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
        return CreateLayoutCore(
            favoriteState,
            showTitle,
            showArtist,
            reserveFavoriteSlot: false);
    }

    public static OverlayTrackContentLayout CreateLayoutForTrack(
        TrackInfo? track,
        OverlayFavoriteVisualState favoriteState,
        bool showTitle,
        bool showArtist)
    {
        return CreateLayoutCore(
            favoriteState,
            showTitle,
            showArtist,
            reserveFavoriteSlot:
                IsSupportedCloudMusicSource(track?.SourceAppId));
    }

    private static OverlayTrackContentLayout CreateLayoutCore(
        OverlayFavoriteVisualState favoriteState,
        bool showTitle,
        bool showArtist,
        bool reserveFavoriteSlot)
    {
        if (favoriteState == OverlayFavoriteVisualState.Unavailable &&
            !reserveFavoriteSlot)
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
            ShowFavoriteStar: favoriteState != OverlayFavoriteVisualState.Unavailable,
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
