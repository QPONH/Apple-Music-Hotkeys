using System.IO;
using System.Text.Json;
using AppleMusicOverlay.Models;

namespace AppleMusicOverlay.Services;

public sealed record CloudMusicLikedPlaylistCandidate(long PlaylistId, long CacheTimestamp);

public interface ICloudMusicFavoriteStateReader : IOverlayFavoriteStateReader
{
    Task<OverlayFavoriteVisualState> ReadAsync(
        TrackInfo track,
        bool forceRefresh,
        CancellationToken cancellationToken = default);
}

public sealed class CloudMusicFavoriteStateService : ICloudMusicFavoriteStateReader
{
    private readonly string _playingListPath;
    private readonly string _webDatabasePath;
    private readonly IReadOnlySqliteTextQuery _sqliteQuery;
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private readonly object _cacheLock = new();
    private FileStamp _playingListStamp;
    private FileStamp _webDatabaseStamp;
    private string? _cachedTrackIdentity;
    private long? _cachedTrackId;
    private long? _likedPlaylistId;
    private HashSet<long>? _likedTrackIds;

    public CloudMusicFavoriteStateService(
        string? playingListPath = null,
        string? webDatabasePath = null,
        IReadOnlySqliteTextQuery? sqliteQuery = null)
    {
        string cloudMusicRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NetEase",
            "CloudMusic");
        _playingListPath = playingListPath ?? Path.Combine(cloudMusicRoot, "webdata", "file", "playingList");
        _webDatabasePath = webDatabasePath ?? Path.Combine(cloudMusicRoot, "Library", "webdb.dat");
        _sqliteQuery = sqliteQuery ?? new WindowsSqliteTextQuery();
    }

    public bool IsSupportedSource(string? sourceAppId)
    {
        return OverlayFavoritePresentation.IsSupportedCloudMusicSource(sourceAppId);
    }

    public async Task<OverlayFavoriteVisualState> ReadAsync(
        TrackInfo track,
        CancellationToken cancellationToken)
    {
        return await ReadAsync(track, forceRefresh: false, cancellationToken);
    }

    public async Task<OverlayFavoriteVisualState> ReadAsync(
        TrackInfo track,
        bool forceRefresh,
        CancellationToken cancellationToken = default)
    {
        if (!IsSupportedSource(track.SourceAppId))
        {
            return OverlayFavoriteVisualState.Unavailable;
        }

        await _readGate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(
                () => ReadCore(track, forceRefresh, cancellationToken),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return OverlayFavoriteVisualState.Unavailable;
        }
        finally
        {
            _readGate.Release();
        }
    }

    private OverlayFavoriteVisualState ReadCore(
        TrackInfo track,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FileStamp playingListStamp = FileStamp.Read(_playingListPath);
        FileStamp databaseStamp = FileStamp.Read(_webDatabasePath);
        if (!playingListStamp.Exists || !databaseStamp.Exists)
        {
            return OverlayFavoriteVisualState.Unavailable;
        }

        long? trackId = ResolveTrackId(track, playingListStamp, cancellationToken);
        if (trackId == null)
        {
            return OverlayFavoriteVisualState.Unavailable;
        }

        HashSet<long>? likedTrackIds = ResolveLikedTrackIds(
            databaseStamp,
            forceRefresh,
            cancellationToken);
        if (likedTrackIds == null)
        {
            return OverlayFavoriteVisualState.Unavailable;
        }

        return likedTrackIds.Contains(trackId.Value)
            ? OverlayFavoriteVisualState.Favorite
            : OverlayFavoriteVisualState.NotFavorite;
    }

    private long? ResolveTrackId(
        TrackInfo track,
        FileStamp playingListStamp,
        CancellationToken cancellationToken)
    {
        string trackIdentity = TrackIdentity.Create(track);
        lock (_cacheLock)
        {
            if (_playingListStamp == playingListStamp &&
                string.Equals(_cachedTrackIdentity, trackIdentity, StringComparison.Ordinal))
            {
                return _cachedTrackId;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        using FileStream stream = File.Open(
            _playingListPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using JsonDocument document = JsonDocument.Parse(stream);
        long? trackId = CloudMusicPlayingListArtworkFinder.FindTrackId(
            document.RootElement,
            track.Title,
            track.Artist,
            track.Duration);

        lock (_cacheLock)
        {
            _playingListStamp = playingListStamp;
            _cachedTrackIdentity = trackIdentity;
            _cachedTrackId = trackId;
        }

        return trackId;
    }

    private HashSet<long>? ResolveLikedTrackIds(
        FileStamp databaseStamp,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        long? cachedPlaylistId;
        lock (_cacheLock)
        {
            if (!forceRefresh &&
                _webDatabaseStamp == databaseStamp &&
                _likedTrackIds != null)
            {
                return _likedTrackIds;
            }

            cachedPlaylistId = _likedPlaylistId;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (cachedPlaylistId != null)
        {
            HashSet<long>? cachedPlaylistTracks = ReadPlaylistTrackIds(cachedPlaylistId.Value);
            if (cachedPlaylistTracks != null)
            {
                CacheLikedTracks(databaseStamp, cachedPlaylistId.Value, cachedPlaylistTracks);
                return cachedPlaylistTracks;
            }
        }

        IReadOnlyList<string?[]> requestRows = _sqliteQuery.Query(
            _webDatabasePath,
            "SELECT id, jsonStr FROM requestCache WHERE jsonStr LIKE '%specialType%'");
        IReadOnlyList<string?[]> playlistRows = _sqliteQuery.Query(
            _webDatabasePath,
            "SELECT id, jsonStr FROM playlistTrackIds");
        Dictionary<long, string> playlistJsonById = playlistRows
            .Where(row => row.Length >= 2 &&
                          long.TryParse(row[0], out _) &&
                          !string.IsNullOrWhiteSpace(row[1]))
            .ToDictionary(
                row => long.Parse(row[0]!),
                row => row[1]!,
                EqualityComparer<long>.Default);

        CloudMusicLikedPlaylistCandidate[] candidates = requestRows
            .Where(row => row.Length >= 2 && !string.IsNullOrWhiteSpace(row[1]))
            .SelectMany(row => CloudMusicFavoriteDatabaseParser.FindLikedPlaylistCandidates(
                row[1]!,
                CloudMusicFavoriteDatabaseParser.ParseCacheTimestamp(row[0])))
            .OrderByDescending(candidate => candidate.CacheTimestamp)
            .DistinctBy(candidate => candidate.PlaylistId)
            .ToArray();

        foreach (CloudMusicLikedPlaylistCandidate candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!playlistJsonById.TryGetValue(candidate.PlaylistId, out string? playlistJson))
            {
                continue;
            }

            HashSet<long>? trackIds = CloudMusicFavoriteDatabaseParser.ParseTrackIds(playlistJson);
            if (trackIds == null)
            {
                continue;
            }

            CacheLikedTracks(databaseStamp, candidate.PlaylistId, trackIds);
            return trackIds;
        }

        return null;
    }

    private HashSet<long>? ReadPlaylistTrackIds(long playlistId)
    {
        IReadOnlyList<string?[]> rows = _sqliteQuery.Query(
            _webDatabasePath,
            $"SELECT jsonStr FROM playlistTrackIds WHERE id = '{playlistId}' LIMIT 1");
        return rows.Count == 1 && rows[0].Length >= 1 && !string.IsNullOrWhiteSpace(rows[0][0])
            ? CloudMusicFavoriteDatabaseParser.ParseTrackIds(rows[0][0]!)
            : null;
    }

    private void CacheLikedTracks(
        FileStamp databaseStamp,
        long playlistId,
        HashSet<long> trackIds)
    {
        lock (_cacheLock)
        {
            _webDatabaseStamp = databaseStamp;
            _likedPlaylistId = playlistId;
            _likedTrackIds = trackIds;
        }
    }

    private readonly record struct FileStamp(bool Exists, long Length, DateTime LastWriteTimeUtc)
    {
        public static FileStamp Read(string path)
        {
            var file = new FileInfo(path);
            file.Refresh();
            return file.Exists
                ? new FileStamp(true, file.Length, file.LastWriteTimeUtc)
                : default;
        }
    }
}

public static class CloudMusicFavoriteDatabaseParser
{
    public static IReadOnlyList<CloudMusicLikedPlaylistCandidate> FindLikedPlaylistCandidates(
        string json,
        long cacheTimestamp)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            var candidates = new List<CloudMusicLikedPlaylistCandidate>();
            CollectLikedPlaylistCandidates(document.RootElement, cacheTimestamp, candidates, depth: 0);
            return candidates;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static HashSet<long>? ParseTrackIds(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("trackIds", out JsonElement trackIds) ||
                trackIds.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var result = new HashSet<long>();
            foreach (JsonElement entry in trackIds.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object &&
                    entry.TryGetProperty("id", out JsonElement id) &&
                    TryGetInt64(id, out long trackId) &&
                    trackId > 0)
                {
                    result.Add(trackId);
                }
            }

            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static long ParseCacheTimestamp(string? requestCacheId)
    {
        if (string.IsNullOrWhiteSpace(requestCacheId))
        {
            return 0;
        }

        int separator = requestCacheId.LastIndexOf('-');
        return separator >= 0 &&
               long.TryParse(requestCacheId[(separator + 1)..], out long timestamp)
            ? timestamp
            : 0;
    }

    private static void CollectLikedPlaylistCandidates(
        JsonElement element,
        long cacheTimestamp,
        ICollection<CloudMusicLikedPlaylistCandidate> candidates,
        int depth)
    {
        if (depth > 32)
        {
            return;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("specialType", out JsonElement specialType) &&
                TryGetInt64(specialType, out long specialTypeValue) &&
                specialTypeValue == 5 &&
                element.TryGetProperty("id", out JsonElement id) &&
                TryGetInt64(id, out long playlistId) &&
                playlistId > 0)
            {
                candidates.Add(new CloudMusicLikedPlaylistCandidate(playlistId, cacheTimestamp));
            }

            foreach (JsonProperty property in element.EnumerateObject())
            {
                CollectLikedPlaylistCandidates(property.Value, cacheTimestamp, candidates, depth + 1);
            }

            return;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                CollectLikedPlaylistCandidates(item, cacheTimestamp, candidates, depth + 1);
            }

            return;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            string? nestedJson = element.GetString();
            if (string.IsNullOrWhiteSpace(nestedJson) ||
                !nestedJson.Contains("specialType", StringComparison.Ordinal) ||
                (nestedJson[0] != '{' && nestedJson[0] != '['))
            {
                return;
            }

            try
            {
                using JsonDocument nestedDocument = JsonDocument.Parse(nestedJson);
                CollectLikedPlaylistCandidates(
                    nestedDocument.RootElement,
                    cacheTimestamp,
                    candidates,
                    depth + 1);
            }
            catch (JsonException)
            {
            }
        }
    }

    private static bool TryGetInt64(JsonElement element, out long value)
    {
        if (element.ValueKind == JsonValueKind.Number)
        {
            return element.TryGetInt64(out value);
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            return long.TryParse(element.GetString(), out value);
        }

        value = 0;
        return false;
    }
}
