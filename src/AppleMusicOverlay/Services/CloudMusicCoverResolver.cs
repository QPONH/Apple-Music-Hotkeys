using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace AppleMusicOverlay.Services;

public interface ICloudMusicCoverResolver
{
    bool IsSupportedSource(string sourceAppId);

    byte[]? TryGetCachedCover(
        string title,
        string artist,
        TimeSpan duration);

    Task<bool> WarmCacheAsync(
        string title,
        string artist,
        TimeSpan duration,
        CancellationToken cancellationToken = default);
}

public sealed class CloudMusicCoverResolver : ICloudMusicCoverResolver
{
    // The overlay renders artwork at 176 px. A 400 px official source retains
    // more than 2x pixel density while accepting legacy albums capped at 430 px.
    public const int MinimumHighResolutionPixels = 400;
    private const int RequestedArtworkSize = 768;
    private const int MaximumArtworkBytes = 3 * 1024 * 1024;
    private const int MaximumCachedCovers = 24;
    private const int NearbyArtworkPrefetchCount = 4;
    private const int PlayingListLookupAttempts = 3;
    private const int ArtworkDownloadAttempts = 2;
    private static readonly TimeSpan ResolutionTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ArtworkRequestTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FailedResolutionRetryDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan PlayingListLookupRetryDelay = TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan ArtworkDownloadRetryDelay = TimeSpan.FromMilliseconds(80);
    private static readonly HttpClient SharedHttpClient = new();
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, byte[]> _coverCache = new(StringComparer.Ordinal);
    private readonly Queue<string> _cacheOrder = new();
    private readonly Dictionary<string, DateTimeOffset> _retryNotBefore = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<byte[]?>> _artworkDownloads = new(StringComparer.Ordinal);
    private readonly HttpClient _httpClient;
    private readonly string _playingListPath;

    public CloudMusicCoverResolver(string? playingListPath = null, HttpClient? httpClient = null)
    {
        _playingListPath = playingListPath ?? GetDefaultPlayingListPath();
        _httpClient = httpClient ?? SharedHttpClient;
    }

    public bool IsSupportedSource(string sourceAppId)
    {
        return sourceAppId.Equals("cloudmusic.exe", StringComparison.OrdinalIgnoreCase) ||
               sourceAppId.EndsWith("\\cloudmusic.exe", StringComparison.OrdinalIgnoreCase) ||
               sourceAppId.EndsWith("/cloudmusic.exe", StringComparison.OrdinalIgnoreCase);
    }

    public byte[]? TryGetCachedCover(
        string title,
        string artist,
        TimeSpan duration)
    {
        Uri? artworkUri = TryFindArtworkUri(title, artist, duration);
        if (artworkUri == null)
        {
            return null;
        }

        string key = CreateArtworkCacheKey(artworkUri);
        lock (_cacheLock)
        {
            return _coverCache.TryGetValue(key, out byte[]? coverBytes) ? coverBytes : null;
        }
    }

    public async Task<bool> WarmCacheAsync(
        string title,
        string artist,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(ResolutionTimeout);
        bool cacheWarmed = false;
        string? cacheKey = null;

        try
        {
            ArtworkLookup? lookup = await TryFindArtworkWithRetryAsync(
                title,
                artist,
                duration,
                timeoutSource.Token);
            if (lookup == null)
            {
                return false;
            }

            cacheKey = CreateArtworkCacheKey(lookup.CurrentArtworkUri);
            if (TryGetCachedCover(cacheKey) is { Length: > 0 })
            {
                StartNearbyPrefetch(lookup.NearbyArtworkUris);
                return true;
            }

            if (!CanAttemptResolution(cacheKey))
            {
                return false;
            }

            IReadOnlyList<Uri> requestUris = CreateArtworkRequestUris(lookup.CurrentArtworkUri);
            if (requestUris.Count == 0)
            {
                return false;
            }

            byte[]? candidateBytes = await GetOrStartArtworkDownload(
                    cacheKey,
                    requestUris)
                .WaitAsync(timeoutSource.Token);
            if (candidateBytes == null)
            {
                return false;
            }

            StartNearbyPrefetch(lookup.NearbyArtworkUris);
            cacheWarmed = true;
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (!cacheWarmed && cacheKey != null)
            {
                DelayRetry(cacheKey);
            }
        }
    }

    private Uri? TryFindArtworkUri(
        string title,
        string artist,
        TimeSpan duration)
    {
        try
        {
            using FileStream stream = File.Open(
                _playingListPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using JsonDocument document = JsonDocument.Parse(stream);
            return CloudMusicPlayingListArtworkFinder.Find(
                document.RootElement,
                title,
                artist,
                duration);
        }
        catch
        {
            return null;
        }
    }

    private async Task<ArtworkLookup?> TryFindArtworkAsync(
        string title,
        string artist,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        using FileStream stream = File.Open(
            _playingListPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using JsonDocument document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken);
        Uri? currentArtworkUri = CloudMusicPlayingListArtworkFinder.Find(
            document.RootElement,
            title,
            artist,
            duration);
        if (currentArtworkUri == null)
        {
            return null;
        }

        IReadOnlyList<Uri> nearbyArtworkUris =
            CloudMusicPlayingListArtworkFinder.FindFollowingArtwork(
                document.RootElement,
                title,
                artist,
                duration,
                NearbyArtworkPrefetchCount);
        return new ArtworkLookup(currentArtworkUri, nearbyArtworkUris);
    }

    private async Task<ArtworkLookup?> TryFindArtworkWithRetryAsync(
        string title,
        string artist,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < PlayingListLookupAttempts; attempt++)
        {
            try
            {
                ArtworkLookup? lookup = await TryFindArtworkAsync(
                    title,
                    artist,
                    duration,
                    cancellationToken);
                if (lookup != null)
                {
                    return lookup;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch when (attempt < PlayingListLookupAttempts - 1)
            {
            }

            if (attempt < PlayingListLookupAttempts - 1)
            {
                await Task.Delay(
                    PlayingListLookupRetryDelay * (attempt + 1),
                    cancellationToken);
            }
        }

        return null;
    }

    private async Task<byte[]?> TryDownloadArtworkWithRetryAsync(
        Uri requestUri,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < ArtworkDownloadAttempts; attempt++)
        {
            try
            {
                byte[]? artworkBytes = await TryDownloadArtworkAsync(
                    requestUri,
                    cancellationToken);
                if (artworkBytes != null)
                {
                    return artworkBytes;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch when (attempt < ArtworkDownloadAttempts - 1)
            {
            }

            if (attempt < ArtworkDownloadAttempts - 1)
            {
                await Task.Delay(ArtworkDownloadRetryDelay, cancellationToken);
            }
        }

        return null;
    }

    private async Task<byte[]?> TryDownloadArtworkAsync(Uri requestUri, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _httpClient.GetAsync(
            requestUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK ||
            response.Content.Headers.ContentLength is > MaximumArtworkBytes ||
            response.Content.Headers.ContentType?.MediaType?.StartsWith(
                "image/",
                StringComparison.OrdinalIgnoreCase) != true)
        {
            return null;
        }

        await using Stream responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        byte[] buffer = new byte[81920];
        while (true)
        {
            int bytesRead = await responseStream.ReadAsync(buffer, cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            if (output.Length + bytesRead > MaximumArtworkBytes)
            {
                return null;
            }

            output.Write(buffer, 0, bytesRead);
        }

        return output.Length > 0 ? output.ToArray() : null;
    }

    private byte[]? TryGetCachedCover(string cacheKey)
    {
        lock (_cacheLock)
        {
            return _coverCache.TryGetValue(cacheKey, out byte[]? coverBytes)
                ? coverBytes
                : null;
        }
    }

    private void StartNearbyPrefetch(IReadOnlyList<Uri> artworkUris)
    {
        if (artworkUris.Count == 0)
        {
            return;
        }

        _ = PrefetchNearbyAsync(artworkUris);
    }

    private async Task PrefetchNearbyAsync(IReadOnlyList<Uri> artworkUris)
    {
        foreach (Uri artworkUri in artworkUris)
        {
            string cacheKey = CreateArtworkCacheKey(artworkUri);
            if (!CanAttemptResolution(cacheKey))
            {
                continue;
            }

            try
            {
                IReadOnlyList<Uri> requestUris = CreateArtworkRequestUris(artworkUri);
                if (requestUris.Count == 0)
                {
                    continue;
                }

                await GetOrStartArtworkDownload(cacheKey, requestUris);
            }
            catch
            {
            }
        }
    }

    private Task<byte[]?> GetOrStartArtworkDownload(
        string cacheKey,
        IReadOnlyList<Uri> requestUris)
    {
        TaskCompletionSource<byte[]?>? completion = null;
        lock (_cacheLock)
        {
            if (_coverCache.TryGetValue(cacheKey, out byte[]? cachedCover))
            {
                return Task.FromResult<byte[]?>(cachedCover);
            }

            if (_artworkDownloads.TryGetValue(cacheKey, out Task<byte[]?>? activeDownload))
            {
                return activeDownload;
            }

            completion = new TaskCompletionSource<byte[]?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _artworkDownloads[cacheKey] = completion.Task;
        }

        _ = DownloadAndCacheArtworkAsync(cacheKey, requestUris, completion);
        return completion.Task;
    }

    private async Task DownloadAndCacheArtworkAsync(
        string cacheKey,
        IReadOnlyList<Uri> requestUris,
        TaskCompletionSource<byte[]?> completion)
    {
        byte[]? candidateBytes = null;
        try
        {
            foreach (Uri requestUri in requestUris)
            {
                using var timeoutSource = new CancellationTokenSource(ArtworkRequestTimeout);
                byte[]? downloadedBytes;
                try
                {
                    downloadedBytes = await TryDownloadArtworkWithRetryAsync(
                        requestUri,
                        timeoutSource.Token);
                }
                catch (OperationCanceledException)
                {
                    continue;
                }

                if (downloadedBytes == null ||
                    !CoverImageQuality.MeetsMinimumResolution(
                        downloadedBytes,
                        MinimumHighResolutionPixels))
                {
                    continue;
                }

                candidateBytes = downloadedBytes;
                CacheCover(cacheKey, downloadedBytes);
                break;
            }
        }
        catch
        {
        }
        finally
        {
            lock (_cacheLock)
            {
                _artworkDownloads.Remove(cacheKey);
            }

            completion.TrySetResult(candidateBytes);
        }
    }

    private void CacheCover(string key, byte[] coverBytes)
    {
        lock (_cacheLock)
        {
            _retryNotBefore.Remove(key);
            if (_coverCache.ContainsKey(key))
            {
                return;
            }

            _coverCache[key] = coverBytes;
            _cacheOrder.Enqueue(key);
            while (_coverCache.Count > MaximumCachedCovers && _cacheOrder.TryDequeue(out string? oldestKey))
            {
                _coverCache.Remove(oldestKey);
            }
        }
    }

    private bool CanAttemptResolution(string key)
    {
        lock (_cacheLock)
        {
            if (_coverCache.ContainsKey(key))
            {
                return false;
            }

            if (_retryNotBefore.TryGetValue(key, out DateTimeOffset retryAt))
            {
                if (retryAt > DateTimeOffset.UtcNow)
                {
                    return false;
                }

                _retryNotBefore.Remove(key);
            }

            return true;
        }
    }

    private void DelayRetry(string key)
    {
        lock (_cacheLock)
        {
            if (_coverCache.ContainsKey(key))
            {
                return;
            }

            _retryNotBefore[key] = DateTimeOffset.UtcNow + FailedResolutionRetryDelay;
            while (_retryNotBefore.Count > MaximumCachedCovers)
            {
                KeyValuePair<string, DateTimeOffset> oldest =
                    _retryNotBefore.MinBy(pair => pair.Value);
                _retryNotBefore.Remove(oldest.Key);
            }
        }
    }

    private static IReadOnlyList<Uri> CreateArtworkRequestUris(Uri? artworkUri)
    {
        if (artworkUri == null ||
            (artworkUri.Scheme != Uri.UriSchemeHttp && artworkUri.Scheme != Uri.UriSchemeHttps) ||
            (!artworkUri.Host.Equals("music.126.net", StringComparison.OrdinalIgnoreCase) &&
             !artworkUri.Host.EndsWith(".music.126.net", StringComparison.OrdinalIgnoreCase)))
        {
            return [];
        }

        Uri resizedHttpsUri = new UriBuilder(artworkUri)
        {
            Scheme = Uri.UriSchemeHttps,
            Port = -1,
            Query = $"param={RequestedArtworkSize}y{RequestedArtworkSize}"
        }.Uri;

        if (artworkUri.Scheme != Uri.UriSchemeHttp)
        {
            return [resizedHttpsUri];
        }

        // Some legacy NetEase objects only exist at the exact HTTP URL from
        // playingList. Keep HTTPS as the fast/default path, then fall back to
        // that official URL without adding a resize query.
        return [resizedHttpsUri, artworkUri];
    }

    private static string CreateArtworkCacheKey(Uri artworkUri)
    {
        return artworkUri.GetLeftPart(UriPartial.Path);
    }

    private static string GetDefaultPlayingListPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NetEase",
            "CloudMusic",
            "webdata",
            "file",
            "playingList");
    }

    private sealed record ArtworkLookup(
        Uri CurrentArtworkUri,
        IReadOnlyList<Uri> NearbyArtworkUris);
}

public static class CloudMusicPlayingListArtworkFinder
{
    private const double DurationToleranceMilliseconds = 1500;
    private static readonly char[] ArtistSeparators = [',', ';', '/', '\\', '\u3001', '&', '\uFF06', '|'];

    public static Uri? Find(JsonElement root, string title, string artist, TimeSpan duration)
    {
        if (!root.TryGetProperty("list", out JsonElement list) ||
            list.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string normalizedTitle = NormalizeIdentity(title);
        string normalizedArtist = NormalizeIdentity(artist);
        if (normalizedTitle.Length == 0 || normalizedArtist.Length == 0)
        {
            return null;
        }

        var exactArtistCandidates = new List<ArtworkCandidate>();
        var partialArtistCandidates = new List<ArtworkCandidate>();
        foreach (JsonElement item in list.EnumerateArray())
        {
            if (!item.TryGetProperty("track", out JsonElement track) ||
                !TryGetString(track, "name", out string trackName) ||
                !NormalizeIdentity(trackName).Equals(normalizedTitle, StringComparison.Ordinal) ||
                !TryGetArtworkUri(track, out Uri artworkUri))
            {
                continue;
            }

            long durationMilliseconds = TryGetInt64(track, "duration");
            var candidate = new ArtworkCandidate(artworkUri, durationMilliseconds);
            if (ArtistsMatch(track, artist, normalizedArtist))
            {
                exactArtistCandidates.Add(candidate);
            }
            else if (ArtistsContainSmtcArtist(track, artist))
            {
                partialArtistCandidates.Add(candidate);
            }
        }

        ArtworkCandidate[] durationMatches = FilterByDuration(
            exactArtistCandidates,
            duration);
        if (durationMatches.Length == 0)
        {
            ArtworkCandidate[] partialDurationMatches = FilterByDuration(
                partialArtistCandidates,
                duration);
            if (partialDurationMatches.Length != 1)
            {
                return null;
            }

            durationMatches = partialDurationMatches;
        }

        if (durationMatches.Length == 0)
        {
            return null;
        }

        string[] distinctPaths = durationMatches
            .Select(candidate => candidate.ArtworkUri.AbsolutePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return distinctPaths.Length == 1 ? durationMatches[0].ArtworkUri : null;
    }

    public static IReadOnlyList<Uri> FindFollowingArtwork(
        JsonElement root,
        string title,
        string artist,
        TimeSpan duration,
        int count)
    {
        if (count <= 0 ||
            !root.TryGetProperty("list", out JsonElement list) ||
            list.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        Uri? currentArtworkUri = Find(root, title, artist, duration);
        if (currentArtworkUri == null)
        {
            return [];
        }

        string normalizedTitle = NormalizeIdentity(title);
        string normalizedArtist = NormalizeIdentity(artist);
        JsonElement[] items = list.EnumerateArray().ToArray();
        int currentIndex = -1;
        for (int index = 0; index < items.Length; index++)
        {
            if (!items[index].TryGetProperty("track", out JsonElement track) ||
                !TryGetString(track, "name", out string trackName) ||
                !NormalizeIdentity(trackName).Equals(normalizedTitle, StringComparison.Ordinal) ||
                (!ArtistsMatch(track, artist, normalizedArtist) &&
                 !ArtistsContainSmtcArtist(track, artist)) ||
                !TryGetArtworkUri(track, out Uri artworkUri) ||
                !artworkUri.AbsolutePath.Equals(
                    currentArtworkUri.AbsolutePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            long durationMilliseconds = TryGetInt64(track, "duration");
            if (duration > TimeSpan.Zero &&
                durationMilliseconds > 0 &&
                Math.Abs(durationMilliseconds - duration.TotalMilliseconds) >
                DurationToleranceMilliseconds)
            {
                continue;
            }

            currentIndex = index;
            break;
        }

        if (currentIndex < 0 || items.Length <= 1)
        {
            return [];
        }

        var result = new List<Uri>(count);
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            currentArtworkUri.AbsolutePath
        };
        for (int offset = 1; offset < items.Length && result.Count < count; offset++)
        {
            int index = (currentIndex + offset) % items.Length;
            if (!items[index].TryGetProperty("track", out JsonElement track) ||
                !TryGetArtworkUri(track, out Uri artworkUri) ||
                !seenPaths.Add(artworkUri.AbsolutePath))
            {
                continue;
            }

            result.Add(artworkUri);
        }

        return result;
    }

    public static long? FindTrackId(JsonElement root, string title, string artist, TimeSpan duration)
    {
        if (!root.TryGetProperty("list", out JsonElement list) ||
            list.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string normalizedTitle = NormalizeIdentity(title);
        string normalizedArtist = NormalizeIdentity(artist);
        if (normalizedTitle.Length == 0 || normalizedArtist.Length == 0)
        {
            return null;
        }

        var exactArtistCandidates = new List<TrackIdCandidate>();
        var partialArtistCandidates = new List<TrackIdCandidate>();
        foreach (JsonElement item in list.EnumerateArray())
        {
            if (!item.TryGetProperty("track", out JsonElement track) ||
                !TryGetString(track, "name", out string trackName) ||
                !NormalizeIdentity(trackName).Equals(normalizedTitle, StringComparison.Ordinal) ||
                !TryGetTrackId(track, out long trackId))
            {
                continue;
            }

            var candidate = new TrackIdCandidate(
                trackId,
                TryGetInt64(track, "duration"));
            if (ArtistsMatch(track, artist, normalizedArtist))
            {
                exactArtistCandidates.Add(candidate);
            }
            else if (ArtistsContainSmtcArtist(track, artist))
            {
                partialArtistCandidates.Add(candidate);
            }
        }

        TrackIdCandidate[] durationMatches = FilterByDuration(
            exactArtistCandidates,
            duration);
        if (durationMatches.Length == 0)
        {
            TrackIdCandidate[] partialDurationMatches = FilterByDuration(
                partialArtistCandidates,
                duration);
            if (partialDurationMatches.Length != 1)
            {
                return null;
            }

            durationMatches = partialDurationMatches;
        }

        if (durationMatches.Length == 0)
        {
            return null;
        }

        long[] distinctIds = durationMatches
            .Select(candidate => candidate.TrackId)
            .Distinct()
            .ToArray();
        return distinctIds.Length == 1 ? distinctIds[0] : null;
    }

    public static string NormalizeIdentity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string normalized = value.Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(normalized.Length);
        foreach (char character in normalized)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    private static bool ArtistsMatch(JsonElement track, string smtcArtist, string normalizedSmtcArtist)
    {
        if (!track.TryGetProperty("artists", out JsonElement artists) ||
            artists.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        string[] localArtists = artists
            .EnumerateArray()
            .Select(value => TryGetString(value, "name", out string name)
                ? NormalizeIdentity(name)
                : string.Empty)
            .Where(value => value.Length > 0)
            .ToArray();
        if (localArtists.Length == 0)
        {
            return false;
        }

        string expandedArtist = smtcArtist
            .Replace(" feat. ", "|", StringComparison.OrdinalIgnoreCase)
            .Replace(" feat ", "|", StringComparison.OrdinalIgnoreCase)
            .Replace(" ft. ", "|", StringComparison.OrdinalIgnoreCase)
            .Replace(" ft ", "|", StringComparison.OrdinalIgnoreCase);
        HashSet<string> smtcArtists = expandedArtist
            .Split(ArtistSeparators, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeIdentity)
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        return smtcArtists.Count > 0 &&
               (localArtists.ToHashSet(StringComparer.Ordinal).SetEquals(smtcArtists) ||
                 string.Concat(localArtists).Equals(normalizedSmtcArtist, StringComparison.Ordinal));
    }

    private static bool ArtistsContainSmtcArtist(JsonElement track, string smtcArtist)
    {
        if (!track.TryGetProperty("artists", out JsonElement artists) ||
            artists.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        string expandedArtist = smtcArtist
            .Replace(" feat. ", "|", StringComparison.OrdinalIgnoreCase)
            .Replace(" feat ", "|", StringComparison.OrdinalIgnoreCase)
            .Replace(" ft. ", "|", StringComparison.OrdinalIgnoreCase)
            .Replace(" ft ", "|", StringComparison.OrdinalIgnoreCase);
        HashSet<string> smtcArtists = expandedArtist
            .Split(ArtistSeparators, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeIdentity)
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        HashSet<string> localArtists = artists
            .EnumerateArray()
            .Select(value => TryGetString(value, "name", out string name)
                ? NormalizeIdentity(name)
                : string.Empty)
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        return smtcArtists.Count > 0 &&
               smtcArtists.IsSubsetOf(localArtists);
    }

    private static T[] FilterByDuration<T>(
        IReadOnlyCollection<T> candidates,
        TimeSpan duration)
        where T : IDurationCandidate
    {
        if (candidates.Count == 0)
        {
            return [];
        }

        if (duration <= TimeSpan.Zero ||
            !candidates.Any(candidate => candidate.DurationMilliseconds > 0))
        {
            return candidates.ToArray();
        }

        return candidates
            .Where(candidate =>
                candidate.DurationMilliseconds > 0 &&
                Math.Abs(candidate.DurationMilliseconds - duration.TotalMilliseconds) <=
                DurationToleranceMilliseconds)
            .ToArray();
    }

    private static bool TryGetArtworkUri(JsonElement track, out Uri artworkUri)
    {
        artworkUri = null!;
        if (!track.TryGetProperty("album", out JsonElement album) ||
            !TryGetString(album, "picUrl", out string picUrl) ||
            !Uri.TryCreate(picUrl, UriKind.Absolute, out Uri? parsedArtworkUri))
        {
            return false;
        }

        artworkUri = parsedArtworkUri;
        return true;
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out JsonElement property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return value.Length > 0;
    }

    private static long TryGetInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property))
        {
            return 0;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out long number))
        {
            return number;
        }

        return property.ValueKind == JsonValueKind.String &&
               long.TryParse(property.GetString(), out number)
            ? number
            : 0;
    }

    private static bool TryGetTrackId(JsonElement track, out long trackId)
    {
        trackId = TryGetInt64(track, "id");
        return trackId > 0;
    }

    private interface IDurationCandidate
    {
        long DurationMilliseconds { get; }
    }

    private sealed record ArtworkCandidate(
        Uri ArtworkUri,
        long DurationMilliseconds) : IDurationCandidate;

    private sealed record TrackIdCandidate(
        long TrackId,
        long DurationMilliseconds) : IDurationCandidate;
}

public static class CoverImageQuality
{
    public static bool MeetsMinimumResolution(byte[] imageBytes, int minimumPixels)
    {
        return TryGetPixelSize(imageBytes, out int width, out int height) &&
               width >= minimumPixels &&
               height >= minimumPixels;
    }

    public static bool IsHigherResolution(byte[] candidateBytes, byte[] currentBytes)
    {
        return TryGetPixelSize(candidateBytes, out int candidateWidth, out int candidateHeight) &&
               candidateWidth >= 32 &&
               candidateHeight >= 32 &&
               (!TryGetPixelSize(currentBytes, out int currentWidth, out int currentHeight) ||
                (candidateWidth > currentWidth && candidateHeight > currentHeight));
    }

    private static bool TryGetPixelSize(byte[] bytes, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (bytes.Length == 0)
        {
            return false;
        }

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            BitmapDecoder decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.DelayCreation | BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.None);
            BitmapFrame frame = decoder.Frames[0];
            width = frame.PixelWidth;
            height = frame.PixelHeight;
            return width > 0 && height > 0;
        }
        catch
        {
            return false;
        }
    }
}
