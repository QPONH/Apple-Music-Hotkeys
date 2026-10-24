using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AppleMusicOverlay.Services;

public sealed record LyricLine(TimeSpan Start, string Text, string? Translation = null);
public sealed record LyricsResult(string Title, string Artist, IReadOnlyList<LyricLine> Lines);

/// <summary>Outcome of one lyric lookup.</summary>
public enum LyricsLookupStatus
{
    /// <summary>Lyrics were found for the requested track.</summary>
    Found,
    /// <summary>Every provider answered, but none of them knew this track.</summary>
    NotFound,
    /// <summary>The lookup could not complete (network / rate limit), so a later manual retry may succeed.</summary>
    Unavailable
}

public sealed record LyricsLookupResult(LyricsLookupStatus Status, LyricsResult? Lyrics)
{
    public static LyricsLookupResult Found(LyricsResult lyrics) => new(LyricsLookupStatus.Found, lyrics);
    public static LyricsLookupResult NotFound { get; } = new(LyricsLookupStatus.NotFound, null);
    public static LyricsLookupResult Unavailable { get; } = new(LyricsLookupStatus.Unavailable, null);
}

/// <summary>
/// Looks lyrics up by the metadata Apple Music exposes through SMTC.
/// A single track can be indexed under several spellings ("Song (feat. X)", "Song - Remastered",
/// "Artist & Artist"), so every provider is queried with a small set of title/artist variants and
/// the best search hit is chosen by title, artist and duration instead of "first result wins".
/// Providers run together: a dead LRCLIB connection must not hide lyrics that NetEase or KuGou already have.
/// </summary>
public sealed class LyricsService
{
    private const int MaxCacheEntries = 200;
    private const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36";

    private static readonly object ClientGate = new();
    private static HttpClient _client = CreateClient();
    private static HttpClient Client => _client;

    // Lyrics survive restarts: a song that was looked up once never shows "正在查找歌词" again.
    private static readonly string CacheFilePath = Path.Combine(AppPaths.SettingsDirectory, "lyrics-cache.json");
    private static readonly JsonSerializerOptions CacheJsonOptions = new() { WriteIndented = false };

    private readonly SmtcMediaSessionService _mediaService;
    private readonly object _cacheGate = new();
    private readonly Dictionary<string, LyricsResult> _lyricsCache = new(StringComparer.Ordinal);
    private readonly Queue<string> _cacheOrder = new();
    private readonly HashSet<string> _prefetchAttemptedTracks = new(StringComparer.Ordinal);
    private bool _cacheFileLoaded;
    private int _cacheSaveQueued;
    private int _cacheDirty;

    public LyricsService(SmtcMediaSessionService mediaService)
    {
        _mediaService = mediaService;
        // Warm the on-disk cache at startup so the very first track of a session can also be
        // served without the "正在查找歌词" placeholder.
        LoadCacheFromDisk();
    }

    public Task<PlaybackSnapshot?> GetCurrentPlaybackAsync(CancellationToken token = default) => _mediaService.GetCurrentPlaybackAsync(token);

    /// <summary>
    /// Best-effort prefetch for the next track in the same album's catalog order. SMTC exposes the
    /// current item but not the Apple Music Up Next queue, so this is intentionally only a cache warm-up:
    /// it never changes the current overlay, and a later track is still verified by its own cache key.
    /// </summary>
    public async Task PrefetchNextAlbumTrackLyricsAsync(PlaybackSnapshot current, CancellationToken token = default)
    {
        string title = CleanMetadata(current.Title);
        string artist = CleanMetadata(current.Artist);
        string album = CleanMetadata(current.Album);
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(album)) return;

        string currentKey = BuildCacheKey(title, artist) + "\n" + album.Trim().ToLowerInvariant();
        lock (_cacheGate)
        {
            if (!_prefetchAttemptedTracks.Add(currentKey)) return;
            if (_prefetchAttemptedTracks.Count > 100)
            {
                _prefetchAttemptedTracks.Clear();
                _prefetchAttemptedTracks.Add(currentKey);
            }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            ItunesTrack? next = await FindNextAlbumTrackAsync(title, artist, album, timeout.Token);
            if (next is null || string.IsNullOrWhiteSpace(next.TrackName) || string.IsNullOrWhiteSpace(next.ArtistName)) return;

            string nextKey = BuildCacheKey(next.TrackName, next.ArtistName);
            lock (_cacheGate)
            {
                if (_lyricsCache.ContainsKey(nextKey)) return;
            }

            TimeSpan duration = next.TrackTimeMillis is > 0
                ? TimeSpan.FromMilliseconds(next.TrackTimeMillis.Value)
                : TimeSpan.Zero;
            LyricsLookupResult result = await GetLyricsAsync(next.TrackName, next.ArtistName,
                next.CollectionName ?? album, duration, forceRefresh: false, token: timeout.Token);
            if (result.Lyrics is not null)
                CrashLog.WriteNote("LyricsPrefetch", $"{title} -> {next.TrackName} | cached {result.Lyrics.Lines.Count} lines");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            // Prefetch is opportunistic; leave regular lookup in charge when the next song starts.
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "Lyrics:PrefetchNextAlbumTrack");
        }
    }

    private static async Task<ItunesTrack?> FindNextAlbumTrackAsync(string title, string artist, string album, CancellationToken token)
    {
        string term = Uri.EscapeDataString($"{artist} {album} {title}");
        string searchUrl = $"https://itunes.apple.com/search?term={term}&entity=song&limit=50";
        using HttpResponseMessage searchResponse = await SendAsync(() => BrowserRequest(searchUrl), token);
        if (!searchResponse.IsSuccessStatusCode) return null;
        ItunesSearchResponse? search = await searchResponse.Content.ReadFromJsonAsync<ItunesSearchResponse>(token);
        if (search?.Results is not { Count: > 0 }) return null;

        string wantedTitle = NormalizeForCompare(title);
        string wantedArtist = NormalizeForCompare(artist);
        string wantedAlbum = NormalizeForCompare(album);
        ItunesTrack? matchedCurrent = search.Results
            .Where(track => track.TrackId > 0 && track.CollectionId > 0 && !string.IsNullOrWhiteSpace(track.TrackName)
                && !string.IsNullOrWhiteSpace(track.ArtistName) && !string.IsNullOrWhiteSpace(track.CollectionName))
            .Where(track => NormalizeForCompare(track.TrackName) == wantedTitle
                && CatalogTextMatches(NormalizeForCompare(track.ArtistName), wantedArtist)
                && CatalogTextMatches(NormalizeForCompare(track.CollectionName), wantedAlbum))
            .OrderBy(track => NormalizeForCompare(track.CollectionName) == wantedAlbum ? 0 : 1)
            .FirstOrDefault();
        if (matchedCurrent is null) return null;

        string lookupUrl = $"https://itunes.apple.com/lookup?id={matchedCurrent.CollectionId}&entity=song";
        using HttpResponseMessage albumResponse = await SendAsync(() => BrowserRequest(lookupUrl), token);
        if (!albumResponse.IsSuccessStatusCode) return null;
        ItunesSearchResponse? albumData = await albumResponse.Content.ReadFromJsonAsync<ItunesSearchResponse>(token);
        if (albumData?.Results is not { Count: > 1 }) return null;

        List<ItunesTrack> tracks = albumData.Results
            .Where(track => track.TrackId > 0
                && (string.Equals(track.Kind, "song", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(track.WrapperType, "track", StringComparison.OrdinalIgnoreCase))
                && !string.IsNullOrWhiteSpace(track.TrackName)
                && !string.IsNullOrWhiteSpace(track.ArtistName))
            .OrderBy(track => track.DiscNumber)
            .ThenBy(track => track.TrackNumber)
            .ToList();

        int currentIndex = tracks.FindIndex(track => track.TrackId == matchedCurrent.TrackId);
        if (currentIndex < 0)
        {
            currentIndex = tracks.FindIndex(track => NormalizeForCompare(track.TrackName) == wantedTitle
                && CatalogTextMatches(NormalizeForCompare(track.ArtistName), wantedArtist));
        }
        if (currentIndex < 0 || currentIndex + 1 >= tracks.Count) return null;

        ItunesTrack next = tracks[currentIndex + 1];
        if (string.IsNullOrWhiteSpace(next.TrackName) || string.IsNullOrWhiteSpace(next.ArtistName)) return null;
        return next;
    }

    private static bool CatalogTextMatches(string candidate, string wanted) =>
        candidate.Length > 0 && wanted.Length > 0 && (candidate == wanted
            || (Math.Min(candidate.Length, wanted.Length) >= 4
                && (candidate.Contains(wanted, StringComparison.Ordinal) || wanted.Contains(candidate, StringComparison.Ordinal))));

    public async Task<LyricsLookupResult> GetLyricsAsync(string title, string artist, string? album, TimeSpan duration, bool forceRefresh = false, CancellationToken token = default)
    {
        title = CleanMetadata(title);
        artist = CleanMetadata(artist);
        album = CleanMetadata(album);
        if (string.IsNullOrWhiteSpace(title)) return LyricsLookupResult.NotFound;
        if (duration < TimeSpan.FromSeconds(1) || duration > TimeSpan.FromMinutes(30)) duration = TimeSpan.Zero;

        LoadCacheFromDisk();
        string cacheKey = BuildCacheKey(title, artist);
        if (forceRefresh)
        {
            // A manual refresh must invalidate memory and disk. Otherwise a failed refresh leaves
            // the old answer in lyrics-cache.json and it reappears after the next restart.
            bool cacheEntryRemoved;
            lock (_cacheGate)
            {
                cacheEntryRemoved = _lyricsCache.Remove(cacheKey) || _cacheOrder.Contains(cacheKey);
                if (cacheEntryRemoved)
                {
                    string[] retainedKeys = _cacheOrder.Where(key => !string.Equals(key, cacheKey, StringComparison.Ordinal)).ToArray();
                    _cacheOrder.Clear();
                    foreach (string key in retainedKeys) _cacheOrder.Enqueue(key);
                }
            }
            if (cacheEntryRemoved) SaveCacheToDisk();
        }
        else
        {
            lock (_cacheGate)
            {
                if (_lyricsCache.TryGetValue(cacheKey, out LyricsResult? cached))
                    return LyricsLookupResult.Found(cached);
            }
        }

        LyricsLookupResult lookup = await LookupAsync(title, artist, album, duration, token);
        if (lookup.Lyrics is not null) Remember(cacheKey, lookup.Lyrics);
        return lookup;
    }

    private async Task<LyricsLookupResult> LookupAsync(string title, string artist, string album, TimeSpan duration, CancellationToken token)
    {
        var candidates = BuildCandidates(title, artist).ToList();
        if (candidates.Count == 0) return LyricsLookupResult.NotFound;

        // Providers run concurrently, but the first acceptable result should cancel the others.
        // This avoids continuing several network searches after the overlay already has its lyrics.
        using var lookupCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        CancellationToken lookupToken = lookupCancellation.Token;
        Task<(LrcLibResponse? Data, bool Failed)> lrclibTask = TryLrclibCandidatesAsync(candidates, album, duration, lookupToken);
        Task<(LrcLibResponse? Data, bool Failed)> neteaseTask = TryNeteaseCandidatesAsync(candidates, duration, lookupToken);
        Task<(LrcLibResponse? Data, bool Failed)> kugouTask = TryKugouAsync(title, artist, duration, lookupToken);
        Task[] providerTasks = { lrclibTask, neteaseTask, kugouTask };
        var fallbacks = new[] { neteaseTask, kugouTask };
        // The caller caps each attempt at 3.25 s, so the fallback grace period must be shorter
        // than that limit or the Chinese providers could be canceled before they are considered.
        Task grace = Task.Delay(TimeSpan.FromMilliseconds(1200), lookupToken);

        try
        {
            while (true)
            {
                var pending = new List<Task>();
                if (!lrclibTask.IsCompleted) pending.Add(lrclibTask);
                foreach (Task<(LrcLibResponse? Data, bool Failed)> task in fallbacks)
                    if (!task.IsCompleted) pending.Add(task);
                if (!grace.IsCompleted) pending.Add(grace);
                if (pending.Count == 0 || pending.All(task => task == grace)) break;

                await Task.WhenAny(pending);

                if (lrclibTask.IsCompleted)
                {
                    LyricsLookupResult? found = TryBuild(title, artist, duration, await lrclibTask);
                    if (found is not null) return found;
                }

                if (lrclibTask.IsCompleted || grace.IsCompleted)
                {
                    foreach (Task<(LrcLibResponse? Data, bool Failed)> task in fallbacks)
                    {
                        if (!task.IsCompleted) continue;
                        LyricsLookupResult? found = TryBuild(title, artist, duration, await task);
                        if (found is not null) return found;
                    }
                }

                if (lrclibTask.IsCompleted && fallbacks.All(task => task.IsCompleted)) break;
            }

            LyricsLookupResult? lrclibFound = TryBuild(title, artist, duration, await lrclibTask);
            if (lrclibFound is not null) return lrclibFound;
            foreach (Task<(LrcLibResponse? Data, bool Failed)> task in fallbacks)
            {
                LyricsLookupResult? found = TryBuild(title, artist, duration, await task);
                if (found is not null) return found;
            }

            bool anyFailed = (await lrclibTask).Failed || (await neteaseTask).Failed || (await kugouTask).Failed;
            // lyrics.ovh is plain-text and often slow. Only spend that request on non-CJK tracks,
            // which are the ones it can actually answer, and only after the synced sources missed.
            if (!ContainsCjk(title) && !ContainsCjk(artist))
            {
                (LrcLibResponse? ovh, bool ovhFailed) = await TryLyricsOvhAsync(candidates[0].Title, candidates[0].Artist, lookupToken);
                anyFailed |= ovhFailed;
                LyricsLookupResult? ovhFound = TryBuild(candidates[0].Title, candidates[0].Artist, duration, (ovh, ovhFailed));
                if (ovhFound is not null) return ovhFound;
            }

            return anyFailed ? LyricsLookupResult.Unavailable : LyricsLookupResult.NotFound;
        }
        finally
        {
            lookupCancellation.Cancel();
            // Observe every provider task so a late cancellation/fault cannot become an
            // unobserved task exception. Cancellation is expected when another provider wins.
            try { await Task.WhenAll(providerTasks.Append(grace)).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { CrashLog.Write(ex, "Lyrics:ProviderCompletion"); }
        }
    }

    private static LyricsLookupResult? TryBuild(string title, string artist, TimeSpan duration, (LrcLibResponse? Data, bool Failed) outcome)
    {
        if (outcome.Data is null) return null;
        LyricsLookupResult built = BuildResult(title, artist, outcome.Data, duration);
        return built.Status == LyricsLookupStatus.Found ? built : null;
    }

    private static LyricsLookupResult BuildResult(string title, string artist, LrcLibResponse data, TimeSpan duration)
    {
        List<LyricLine> lines = new();
        if (!string.IsNullOrWhiteSpace(data.SyncedLyrics)) lines = ParseLrc(data.SyncedLyrics);
        // A synced payload that this parser cannot read (or that is only credit tags) used to
        // discard the plain lyrics sitting next to it and report the track as having none.
        if (lines.Count == 0)
        {
            string? plain = !string.IsNullOrWhiteSpace(data.PlainLyrics) ? data.PlainLyrics : StripSyncedToPlain(data.SyncedLyrics);
            lines = SpreadPlainLyrics(plain, duration);
        }

        // Providers (NetEase in particular) answer with a credit-only payload ("作词 : …",
        // "作曲 : …") for tracks whose real lyrics they do not have. Treating that as a hit made
        // the overlay show two lines of credits for the whole song, so ask the next provider.
        lines = WithoutCreditLines(lines);
        if (lines.Count == 0) return LyricsLookupResult.NotFound;

        // NetEase can return a separate timestamped translation lyric (tlyric). Match each
        // translation to the nearest original line; other providers simply leave this empty.
        if (!string.IsNullOrWhiteSpace(data.TranslationLyrics))
        {
            List<LyricLine> translated = ParseLrc(data.TranslationLyrics);
            if (translated.Count > 0)
                lines = AttachTranslations(lines, translated);
            else
            {
                string[] plainTranslations = data.TranslationLyrics.Replace("\r", string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (plainTranslations.Length == lines.Count)
                    lines = lines.Select((line, index) => line with { Translation = plainTranslations[index] }).ToList();
            }
        }
        return LyricsLookupResult.Found(new LyricsResult(title, artist, lines));
    }

    /// <summary>Matches sorted source/translation timelines in linear time instead of sorting the
    /// entire translation list again for every lyric line.</summary>
    private static List<LyricLine> AttachTranslations(IReadOnlyList<LyricLine> lines, IReadOnlyList<LyricLine> translations)
    {
        var result = new List<LyricLine>(lines.Count);
        int translationIndex = 0;
        foreach (LyricLine line in lines)
        {
            while (translationIndex + 1 < translations.Count
                && Math.Abs((translations[translationIndex + 1].Start - line.Start).TotalMilliseconds)
                    < Math.Abs((translations[translationIndex].Start - line.Start).TotalMilliseconds))
            {
                translationIndex++;
            }

            LyricLine candidate = translations[translationIndex];
            result.Add(Math.Abs((candidate.Start - line.Start).TotalSeconds) <= 2.0
                ? line with { Translation = candidate.Text }
                : line);
        }
        return result;
    }

    /// <summary>Metadata lines that are never sung lyrics.</summary>
    private static readonly Regex CreditLinePattern = new(
        @"^\s*(?:作词|作詞|作曲|编曲|編曲|制作人?|製作人?|监制|監製|混音|母带|母帶|录音|錄音|和声|和聲|吉他|贝斯|貝斯|鼓|键盘|鍵盤|词|詞|曲|演唱|出品|发行|發行|统筹|統籌|企划|企劃|OP|SP|Lyrics?|Composer|Arranger|Producer|Written\s+by|Music\s+by|Mixed\s+by|Mastered\s+by)\s*[:：]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static List<LyricLine> WithoutCreditLines(List<LyricLine> lines)
    {
        if (lines.Count == 0) return lines;
        List<LyricLine> filtered = lines.Where(line => !CreditLinePattern.IsMatch(line.Text)).ToList();
        // A payload that was nothing but credits (or one stray line plus credits) is not lyrics.
        if (filtered.Count == 0 || (lines.Count >= 3 && filtered.Count < 2 && filtered.Count * 2 < lines.Count))
            return new List<LyricLine>();
        return filtered;
    }

    #region title / artist variants

    /// <summary>
    /// Candidate title/artist pairs, most faithful first. The caller stops at the first pair that
    /// yields lyrics, so a track whose metadata is already exact costs a single request.
    /// </summary>
    private static IEnumerable<(string Title, string Artist)> BuildCandidates(string title, string artist)
    {
        List<string> titles = BuildTitleVariants(title);
        List<string> artists = BuildArtistVariants(artist);
        int yielded = 0;
        foreach (string candidateTitle in titles)
        {
            foreach (string candidateArtist in artists)
            {
                yield return (candidateTitle, candidateArtist);
                if (++yielded >= 6) yield break;
            }
        }
    }

    private static List<string> BuildTitleVariants(string title)
    {
        var variants = new List<string>();
        void Add(string? value)
        {
            value = CleanMetadata(value);
            if (string.IsNullOrWhiteSpace(value)) return;
            string cleaned = TrimDecorations(value);
            if (cleaned.Length == 0) return;
            if (!variants.Contains(cleaned, StringComparer.OrdinalIgnoreCase)) variants.Add(cleaned);
        }

        Add(title);

        // "Song (feat. X)" / "Song [Live]" / "Song（中文名）": the bracketed part is decoration.
        string withoutBrackets = Regex.Replace(title, @"\s*[\(\[（【].*?[\)\]）】]\s*", " ");
        Add(withoutBrackets);

        // Unbracketed featuring credits: "Song feat. X".
        string withoutFeaturing = Regex.Replace(withoutBrackets, @"\s+(?:feat\.?|ft\.?|featuring)\s+.*$", string.Empty, RegexOptions.IgnoreCase);
        Add(withoutFeaturing);

        // Apple Music appends a version marker: "Song - Remastered 2011", "Song - Live", "Song - Single".
        Add(Regex.Replace(withoutFeaturing, @"\s+[-–—]\s+.*$", string.Empty));
        Add(Regex.Replace(withoutFeaturing, @"\s*[-–—]?\s*(?:remaster(?:ed)?|deluxe|expanded|anniversary|single|ep|live|radio edit|edit|mix|version)\b.*$", string.Empty, RegexOptions.IgnoreCase));

        return variants.Take(4).ToList();
    }

    private static string TrimDecorations(string value) =>
        value.Trim(' ', '-', '–', '—', '"', '\'', '\u201C', '\u201D', '\u2018', '\u2019', '《', '》');

    private static List<string> BuildArtistVariants(string artist)
    {
        var variants = new List<string>();
        void Add(string? value)
        {
            value = CleanMetadata(value);
            if (string.IsNullOrWhiteSpace(value)) return;
            string cleaned = value.Trim(' ', ',', '&', '/', '-', ';', '、', '／');
            if (cleaned.Length == 0) return;
            if (!variants.Contains(cleaned, StringComparer.OrdinalIgnoreCase)) variants.Add(cleaned);
        }

        Add(artist);

        // Apple Music sometimes reports the whole billing as "Artist — Album - Single"; the
        // primary credit is the part before the dash, so try that first.
        Add(Regex.Split(artist, @"\s+[—–]\s+|\s+-\s+")[0]);

        // "A & B", "A, B", "A、B", "A feat. B": providers usually index the main credit only.
        // "/" is intentionally not a separator so band names such as AC/DC survive.
        string primary = Regex.Split(artist, @"\s*(?:,|&|;|、|／|\+|\bfeat\.?|\bft\.?|\bfeaturing\b|\bwith\b|\bx\b|\bvs\.?\b)\s*", RegexOptions.IgnoreCase)[0];
        Add(primary);

        if (variants.Count == 0) variants.Add(string.Empty);
        var bounded = variants.Take(4).ToList();
        // Last resort when the reported artist is unusable (billing strings, remix credits…):
        // providers are then searched by title only and the hit is scored by title + duration.
        bounded.Add(string.Empty);
        return bounded;
    }

    private static string CollapseWhitespace(string value) => Regex.Replace(value, @"\s+", " ").Trim();

    private static string CleanMetadata(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var buffer = new char[value.Length];
        int length = 0;
        foreach (char c in value)
        {
            if (char.IsControl(c) || c is '\u200B' or '\u200C' or '\u200D' or '\uFEFF' or '\u2060') continue;
            buffer[length++] = c;
        }
        return CollapseWhitespace(new string(buffer, 0, length));
    }

    #endregion

    #region HTTP

    /// <summary>
    /// GET/POST with bounded retries. Fresh requests and short backoff recover from intermittent TLS,
    /// stale pooled connections and transient provider errors without leaving the track unresolved.
    /// </summary>
    private static async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> requestFactory, CancellationToken token)
    {
        Exception? last = null;
        const int maxAttempts = 3;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using HttpRequestMessage request = requestFactory();
                request.Version = HttpVersion.Version11;
                request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
                HttpResponseMessage response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                if (attempt < maxAttempts && IsRetriableStatus(response.StatusCode))
                {
                    response.Dispose();
                    await Task.Delay(TimeSpan.FromMilliseconds(350 * attempt), token);
                    continue;
                }
                return response;
            }
            catch (Exception ex) when (attempt < maxAttempts && IsConnectionFailure(ex) && !token.IsCancellationRequested)
            {
                last = ex;
                if (IsTlsFailure(ex)) ResetClient();
                await Task.Delay(TimeSpan.FromMilliseconds(300 * attempt), token);
            }
        }

        throw last ?? new HttpRequestException("歌词服务器连接失败");
    }

    private static bool IsConnectionFailure(Exception ex)
    {
        for (Exception? cursor = ex; cursor is not null; cursor = cursor.InnerException)
        {
            if (cursor is HttpRequestException or IOException or SocketException or TaskCanceledException) return true;
        }
        return false;
    }

    private static bool IsTlsFailure(Exception ex)
    {
        for (Exception? cursor = ex; cursor is not null; cursor = cursor.InnerException)
        {
            if (cursor is System.Security.Authentication.AuthenticationException) return true;
            if (cursor.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase)) return true;
            if (cursor.Message.Contains("TLS", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static void ResetClient()
    {
        lock (ClientGate)
        {
            HttpClient previous = _client;
            _client = CreateClient();
            // In-flight requests still hold the old handler. Dispose it only after they time out.
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(20));
                previous.Dispose();
            });
        }
    }

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(15),
            ConnectTimeout = TimeSpan.FromSeconds(8),
            MaxConnectionsPerServer = 8,
            ConnectCallback = ConnectAsync
        };
        handler.SslOptions.EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
        // LRCLIB asks clients to identify themselves; generic agents are rate limited harder.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MusicFloat/14.35 (https://github.com/QPONH/Apple-Music-Hotkeys)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    /// <summary>Prefer IPv4. A black-holed IPv6 route is a common reason the first handshake never returns.</summary>
    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        DnsEndPoint endpoint = context.DnsEndPoint;
        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(endpoint.Host, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            addresses = Array.Empty<IPAddress>();
        }

        Exception? last = null;
        foreach (IPAddress address in addresses.OrderBy(address => address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1))
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                await socket.ConnectAsync(address, endpoint.Port, timeout.Token);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                last = ex;
                socket.Dispose();
            }
        }

        if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
        throw new HttpRequestException($"无法连接歌词服务器 {endpoint.Host}", last);
    }

    #endregion

    #region LRCLIB

    private static async Task<(LrcLibResponse? Data, bool Failed)> TryLrclibCandidatesAsync(
        IReadOnlyList<(string Title, string Artist)> candidates, string album, TimeSpan duration, CancellationToken token)
    {
        try
        {
            bool failed = false;
            (string title, string artist) = candidates[0];

            if (!string.IsNullOrWhiteSpace(artist))
            {
                (LrcLibResponse? exact, bool exactFailed) = await LrclibGetAsync(title, artist, album, duration, token);
                failed |= exactFailed;
                if (exact is not null) return (exact, false);

                bool narrowed = duration > TimeSpan.Zero || !string.IsNullOrWhiteSpace(album);
                if (narrowed)
                {
                    // Duration or album from SMTC is sometimes just wrong enough to 404 an exact hit.
                    (LrcLibResponse? broad, bool broadFailed) = await LrclibGetAsync(title, artist, string.Empty, TimeSpan.Zero, token);
                    failed |= broadFailed;
                    if (broad is not null) return (broad, false);
                }
            }

            (LrcLibResponse? searched, bool searchFailed) = await SearchLrclibAsync(title, artist, album, duration, token);
            failed |= searchFailed;
            if (searched is not null) return (searched, false);

            for (int i = 1; i < Math.Min(3, candidates.Count); i++)
            {
                if (string.IsNullOrWhiteSpace(candidates[i].Artist)) continue;
                (LrcLibResponse? alt, bool altFailed) = await LrclibGetAsync(candidates[i].Title, candidates[i].Artist, string.Empty, TimeSpan.Zero, token);
                failed |= altFailed;
                if (alt is not null) return (alt, false);
            }

            return (null, failed);
        }
        catch (Exception ex) when (IsTransient(ex) && !token.IsCancellationRequested)
        {
            CrashLog.Write(ex, "Lyrics:LRCLIB");
            return (null, true);
        }
    }

    private static async Task<(LrcLibResponse? Data, bool Failed)> LrclibGetAsync(string title, string artist, string album, TimeSpan duration, CancellationToken token)
    {
        string query = $"track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist)}";
        if (!string.IsNullOrWhiteSpace(album)) query += $"&album_name={Uri.EscapeDataString(album)}";
        if (duration >= TimeSpan.FromSeconds(1) && duration <= TimeSpan.FromMinutes(30))
            query += $"&duration={Math.Round(duration.TotalSeconds).ToString(CultureInfo.InvariantCulture)}";

        using HttpResponseMessage exact = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"https://lrclib.net/api/get?{query}"), token);
        if (!exact.IsSuccessStatusCode) return (null, IsRetriableStatus(exact.StatusCode));

        LrcLibResponse? exactResult = await exact.Content.ReadFromJsonAsync<LrcLibResponse>(token);
        return HasLyrics(exactResult) ? (exactResult, false) : (null, false);
    }

    /// <summary>
    /// Free-text / fielded search. Needed when the exact-match endpoint returns 404 because the
    /// stored title or artist differs slightly from what Apple Music reports.
    /// </summary>
    private static async Task<(LrcLibResponse? Data, bool Failed)> SearchLrclibAsync(string title, string artist, string album, TimeSpan duration, CancellationToken token)
    {
        bool failed = false;
        var urls = new List<string>();
        if (!string.IsNullOrWhiteSpace(artist))
        {
            string albumQuery = string.IsNullOrWhiteSpace(album) ? string.Empty : $"&album_name={Uri.EscapeDataString(album)}";
            urls.Add($"https://lrclib.net/api/search?track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist)}{albumQuery}");
        }
        urls.Add($"https://lrclib.net/api/search?q={Uri.EscapeDataString(string.IsNullOrWhiteSpace(artist) ? title : $"{title} {artist}")}");

        foreach (string url in urls)
        {
            using HttpResponseMessage response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), token);
            if (!response.IsSuccessStatusCode)
            {
                failed |= IsRetriableStatus(response.StatusCode);
                continue;
            }

            List<LrcLibResponse>? results = await response.Content.ReadFromJsonAsync<List<LrcLibResponse>>(token);
            LrcLibResponse? best = PickBest(results, title, artist, duration);
            if (best is not null) return (best, false);
        }

        return (null, failed);
    }

    private static double? ScoreCandidate(string wantedTitle, string wantedArtist, string itemTitle,
        string itemArtist, double? durationDiff) =>
        ScoreCandidate(wantedTitle, itemTitle, MatchesText(itemArtist, wantedArtist), durationDiff);

    private static double? ScoreCandidate(string wantedTitle, string itemTitle, bool artistMatch, double? durationDiff)
    {
        bool titleExact = wantedTitle.Length > 0 && itemTitle == wantedTitle;
        if (!IsPlausibleHit(titleExact || MatchesText(itemTitle, wantedTitle), artistMatch, durationDiff)) return null;
        double score = (titleExact ? -8 : -3) + (artistMatch ? -4 : 6);
        return durationDiff is double diff ? score + Math.Min(diff, 60) * 0.15 : score;
    }

    private static bool MatchesText(string candidate, string wanted) =>
        candidate.Length > 0 && wanted.Length > 0 && (candidate == wanted
            || candidate.Contains(wanted, StringComparison.Ordinal)
            || wanted.Contains(candidate, StringComparison.Ordinal));

    /// <summary>
    /// A hit is only accepted when it carries the playing artist, or when its length matches the
    /// playing track within a couple of seconds. Accepting a same-titled hit from another artist
    /// is what made the overlay show a completely different song's lyrics.
    /// </summary>
    private static bool IsPlausibleHit(bool titleMatches, bool artistMatches, double? durationDiff)
    {
        if (!titleMatches) return false;
        if (artistMatches) return true;
        return durationDiff is double diff && diff <= 2.5;
    }

    /// <summary>Picks the closest hit by title, artist and duration instead of the first row.</summary>
    private static LrcLibResponse? PickBest(List<LrcLibResponse>? results, string title, string artist, TimeSpan duration)
    {
        if (results is null || results.Count == 0) return null;

        string wantedTitle = NormalizeForCompare(title);
        string wantedArtist = NormalizeForCompare(artist);
        double wantedSeconds = duration.TotalSeconds;

        LrcLibResponse? best = null;
        double bestScore = double.MaxValue;

        foreach (LrcLibResponse item in results)
        {
            if (!HasLyrics(item)) continue;

            string itemTitle = NormalizeForCompare(item.TrackName);
            string itemArtist = NormalizeForCompare(item.ArtistName);

            double? durationDiff = wantedSeconds >= 5 && item.Duration is > 0
                ? Math.Abs(item.Duration.Value - wantedSeconds) : null;
            double? score = ScoreCandidate(wantedTitle, wantedArtist, itemTitle, itemArtist, durationDiff);
            if (score is not double candidateScore) continue;

            if (candidateScore < bestScore)
            {
                bestScore = candidateScore;
                best = item;
            }
        }

        return best;
    }

    private static bool HasLyrics(LrcLibResponse? data) =>
        data is not null &&
        (!string.IsNullOrWhiteSpace(data.SyncedLyrics) || !string.IsNullOrWhiteSpace(data.PlainLyrics));

    #endregion

    #region NetEase / KuGou / lyrics.ovh fallbacks

    private static async Task<(LrcLibResponse? Data, bool Failed)> TryNeteaseCandidatesAsync(
        IReadOnlyList<(string Title, string Artist)> candidates, TimeSpan duration, CancellationToken token)
    {
        try
        {
            bool failed = false;
            int tried = 0;
            foreach ((string title, string artist) in candidates)
            {
                if (tried++ >= 2) break;
                (LrcLibResponse? data, bool callFailed) = await TryNeteaseAsync(title, artist, duration, token);
                failed |= callFailed;
                if (data is not null) return (data, false);
            }
            return (null, failed);
        }
        catch (Exception ex) when (IsTransient(ex) && !token.IsCancellationRequested)
        {
            CrashLog.Write(ex, "Lyrics:Netease");
            return (null, true);
        }
    }

    private static async Task<(LrcLibResponse? Data, bool Failed)> TryNeteaseAsync(string title, string artist, TimeSpan duration, CancellationToken token)
    {
        string searchText = string.IsNullOrWhiteSpace(artist) ? title : $"{title} {artist}";
        string[] urls =
        {
            $"https://music.163.com/api/search/get/web?type=1&limit=12&s={Uri.EscapeDataString(searchText)}",
            $"https://music.163.com/api/cloudsearch/pc?type=1&limit=12&s={Uri.EscapeDataString(searchText)}"
        };

        bool failed = false;
        NeteaseSearchResponse? results = null;
        foreach (string url in urls)
        {
            using HttpResponseMessage search = await SendAsync(() => NetEaseRequest(url), token);
            if (!search.IsSuccessStatusCode)
            {
                failed |= IsRetriableStatus(search.StatusCode) || search.StatusCode == HttpStatusCode.Forbidden;
                continue;
            }

            results = await search.Content.ReadFromJsonAsync<NeteaseSearchResponse>(token);
            if (results?.Code is > 0 and not 200) failed = true;
            if (results?.Result?.Songs is { Count: > 0 }) break;
        }

        NeteaseSong? song = PickNeteaseSong(results?.Result?.Songs, title, artist, duration);
        if (song is null) return (null, failed);

        string lyricsUrl = $"https://music.163.com/api/song/lyric?id={song.Id}&lv=-1&kv=-1&tv=-1";
        using HttpResponseMessage lyrics = await SendAsync(() => NetEaseRequest(lyricsUrl), token);
        if (!lyrics.IsSuccessStatusCode) return (null, failed || IsRetriableStatus(lyrics.StatusCode) || lyrics.StatusCode == HttpStatusCode.Forbidden);

        NeteaseLyricsResponse? data = await lyrics.Content.ReadFromJsonAsync<NeteaseLyricsResponse>(token);
        string? synced = data?.Lrc?.Lyric;
        if (string.IsNullOrWhiteSpace(synced)) return (null, failed);
        return (new LrcLibResponse { TrackName = title, ArtistName = artist, SyncedLyrics = synced, TranslationLyrics = data?.Tlyric?.Lyric }, false);
    }

    private static HttpRequestMessage BrowserRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
        return request;
    }

    private static HttpRequestMessage NetEaseRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        // The shared MusicFloat user agent is rejected by NetEase (empty body / 403), which
        // presented as "this song has no lyrics" for the whole Chinese catalog.
        request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
        request.Headers.Referrer = new Uri("https://music.163.com/");
        request.Headers.TryAddWithoutValidation("Cookie", "os=pc; appver=8.9.70");
        return request;
    }

    private static NeteaseSong? PickNeteaseSong(List<NeteaseSong>? songs, string title, string artist, TimeSpan duration)
    {
        if (songs is null || songs.Count == 0) return null;

        string wantedTitle = NormalizeForCompare(title);
        string wantedArtist = NormalizeForCompare(artist);
        double wantedSeconds = duration.TotalSeconds;

        NeteaseSong? best = null;
        double bestScore = double.MaxValue;
        foreach (NeteaseSong song in songs)
        {
            if (song.Id <= 0) continue;

            string itemTitle = NormalizeForCompare(song.Name);
            bool artistMatch = song.ArtistList?.Any(a => MatchesText(NormalizeForCompare(a.Name), wantedArtist)) == true;
            double? durationDiff = wantedSeconds >= 5 && song.LengthMs is > 0
                ? Math.Abs(song.LengthMs.Value / 1000d - wantedSeconds) : null;
            double? score = ScoreCandidate(wantedTitle, itemTitle, artistMatch, durationDiff);
            if (score is not double candidateScore) continue;

            if (candidateScore < bestScore)
            {
                bestScore = candidateScore;
                best = song;
            }
        }

        return best;
    }

    private static async Task<(LrcLibResponse? Data, bool Failed)> TryKugouAsync(string title, string artist, TimeSpan duration, CancellationToken token)
    {
        try
        {
            // Apple Music can report "Artist — Album - Single"; the album part only pollutes the
            // search keyword and made KuGou return nothing.
            string searchArtist = Regex.Split(artist, @"\s+[—–]\s+|\s+-\s+")[0].Trim();
            string keyword = string.IsNullOrWhiteSpace(searchArtist) ? title : $"{searchArtist} {title}";
            string url = "https://songsearch.kugou.com/song_search_v2?page=1&pagesize=8&userid=-1&platform=WebFilter&filter=2&iscorrection=1&privilege_filter=0&keyword="
                + Uri.EscapeDataString(keyword);
            using HttpResponseMessage search = await SendAsync(() => BrowserRequest(url), token);
            if (!search.IsSuccessStatusCode) return (null, IsRetriableStatus(search.StatusCode) || search.StatusCode == HttpStatusCode.Forbidden);

            string body = await search.Content.ReadAsStringAsync(token);
            List<KugouHit> hits = ReadKugouHits(body);
            KugouHit? hit = PickKugouHit(hits, title, searchArtist, duration);
            if (hit is null) return (null, false);

            int durationMs = hit.Value.DurationSeconds > 0
                ? (int)Math.Round(hit.Value.DurationSeconds * 1000)
                : (int)Math.Round(Math.Max(0, duration.TotalMilliseconds));
            string lyricKeyword = string.IsNullOrWhiteSpace(hit.Value.Singer) ? hit.Value.Song : $"{hit.Value.Singer} - {hit.Value.Song}";
            string lyricSearch = $"https://lyrics.kugou.com/search?ver=1&man=yes&client=pc&keyword={Uri.EscapeDataString(lyricKeyword)}&duration={durationMs}&hash={Uri.EscapeDataString(hit.Value.Hash)}";
            using HttpResponseMessage found = await SendAsync(() => BrowserRequest(lyricSearch), token);
            if (!found.IsSuccessStatusCode) return (null, IsRetriableStatus(found.StatusCode));

            using JsonDocument lyricDoc = JsonDocument.Parse(await found.Content.ReadAsStringAsync(token));
            if (!lyricDoc.RootElement.TryGetProperty("candidates", out JsonElement candidates) || candidates.ValueKind != JsonValueKind.Array)
                return (null, false);

            foreach (JsonElement candidate in candidates.EnumerateArray())
            {
                string id = candidate.TryGetProperty("id", out JsonElement idElement) ? idElement.ToString() : string.Empty;
                string accessKey = candidate.TryGetProperty("accesskey", out JsonElement keyElement) ? keyElement.GetString() ?? string.Empty : string.Empty;
                if (id.Length == 0 || accessKey.Length == 0) continue;

                string download = $"https://lyrics.kugou.com/download?ver=1&client=pc&id={Uri.EscapeDataString(id)}&accesskey={Uri.EscapeDataString(accessKey)}&fmt=lrc&charset=utf8";
                using HttpResponseMessage lyric = await SendAsync(() => BrowserRequest(download), token);
                if (!lyric.IsSuccessStatusCode) continue;

                using JsonDocument downloadDoc = JsonDocument.Parse(await lyric.Content.ReadAsStringAsync(token));
                string? content = downloadDoc.RootElement.TryGetProperty("content", out JsonElement contentElement) ? contentElement.GetString() : null;
                string? text = DecodeKugouLyric(content);
                if (!string.IsNullOrWhiteSpace(text))
                    return (new LrcLibResponse { TrackName = hit.Value.Song, ArtistName = hit.Value.Singer, SyncedLyrics = text }, false);
            }

            return (null, false);
        }
        catch (Exception ex) when (IsTransient(ex) && !token.IsCancellationRequested)
        {
            CrashLog.Write(ex, "Lyrics:Kugou");
            return (null, true);
        }
    }

    private readonly record struct KugouHit(string Hash, string Song, string Singer, double DurationSeconds);

    private static List<KugouHit> ReadKugouHits(string json)
    {
        var hits = new List<KugouHit>();
        using JsonDocument doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out JsonElement data)) return hits;
        if (!data.TryGetProperty("lists", out JsonElement list) && !data.TryGetProperty("info", out list)) return hits;
        if (list.ValueKind != JsonValueKind.Array) return hits;

        foreach (JsonElement item in list.EnumerateArray())
        {
            string hash = FirstString(item, "FileHash", "filehash", "hash");
            string song = FirstString(item, "SongName", "songname", "songName");
            string singer = FirstString(item, "SingerName", "singername", "singerName");
            double seconds = FirstDouble(item, "Duration", "duration");
            if (hash.Length == 0 || song.Length == 0) continue;
            hits.Add(new KugouHit(hash, song, singer, seconds));
        }
        return hits;
    }

    private static KugouHit? PickKugouHit(List<KugouHit> hits, string title, string artist, TimeSpan duration)
    {
        if (hits.Count == 0) return null;
        string wantedTitle = NormalizeForCompare(title);
        string wantedArtist = NormalizeForCompare(artist);
        double wantedSeconds = duration.TotalSeconds;

        KugouHit? best = null;
        double bestScore = double.MaxValue;
        foreach (KugouHit hit in hits)
        {
            string itemTitle = NormalizeForCompare(hit.Song);
            string itemArtist = NormalizeForCompare(hit.Singer);

            double? durationDiff = wantedSeconds >= 5 && hit.DurationSeconds > 0
                ? Math.Abs(hit.DurationSeconds - wantedSeconds) : null;
            double? score = ScoreCandidate(wantedTitle, wantedArtist, itemTitle, itemArtist, durationDiff);
            if (score is not double candidateScore) continue;

            if (candidateScore < bestScore)
            {
                bestScore = candidateScore;
                best = hit;
            }
        }
        return best;
    }

    private static string? DecodeKugouLyric(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        string trimmed = content.Trim();
        try
        {
            byte[] bytes = Convert.FromBase64String(trimmed);
            string text = Encoding.UTF8.GetString(bytes);
            if (text.Contains('[', StringComparison.Ordinal) || text.Contains('\n', StringComparison.Ordinal)) return text;
        }
        catch (FormatException)
        {
            // Some responses are already plain LRC.
        }
        return trimmed.Contains('[', StringComparison.Ordinal) ? trimmed : null;
    }

    private static string FirstString(JsonElement item, params string[] names)
    {
        foreach (string name in names)
        {
            if (!item.TryGetProperty(name, out JsonElement value)) continue;
            string? text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
            if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
        }
        return string.Empty;
    }

    private static double FirstDouble(JsonElement item, params string[] names)
    {
        foreach (string name in names)
        {
            if (!item.TryGetProperty(name, out JsonElement value)) continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number)) return number;
            if (value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
                return parsed;
        }
        return 0;
    }

    private static async Task<(LrcLibResponse? Data, bool Failed)> TryLyricsOvhAsync(string title, string artist, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(artist)) return (null, false);

        try
        {
            using HttpResponseMessage response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"https://api.lyrics.ovh/v1/{Uri.EscapeDataString(artist)}/{Uri.EscapeDataString(title)}"), token);
            if (!response.IsSuccessStatusCode) return (null, IsRetriableStatus(response.StatusCode));

            LyricsOvhResponse? result = await response.Content.ReadFromJsonAsync<LyricsOvhResponse>(token);
            return string.IsNullOrWhiteSpace(result?.Lyrics) ? (null, false) : (new LrcLibResponse { PlainLyrics = result.Lyrics }, false);
        }
        catch (Exception ex) when (IsTransient(ex) && !token.IsCancellationRequested)
        {
            CrashLog.Write(ex, "Lyrics:LyricsOvh");
            return (null, true);
        }
    }

    #endregion

    #region parsing / helpers

    private static List<LyricLine> ParseLrc(string lrc)
    {
        var result = new List<LyricLine>();
        int offsetMilliseconds = 0;
        Match offsetMatch = Regex.Match(lrc, @"\[offset:\s*([+-]?\d+)\s*\]", RegexOptions.IgnoreCase);
        if (offsetMatch.Success) int.TryParse(offsetMatch.Groups[1].Value, out offsetMilliseconds);

        // [mm:ss], [mm:ss.xx] and [mm:ss:xx] (colon fraction, common in NetEase / KuGou LRC).
        var timestamp = new Regex(@"\[(\d{1,3}):(\d{1,2}(?:[.:]\d{1,4})?)\]");
        // Enhanced LRC keeps word timings inside the text: "<00:12.34>word".
        var wordTiming = new Regex(@"<\d{1,3}:\d{1,2}(?:[.:]\d{1,4})?>");

        foreach (string raw in lrc.Replace("\r", string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            MatchCollection matches = timestamp.Matches(raw);
            if (matches.Count == 0) continue;

            string text = timestamp.Replace(raw, string.Empty);
            text = wordTiming.Replace(text, string.Empty).Trim();
            if (string.IsNullOrEmpty(text)) continue;

            foreach (Match match in matches)
            {
                if (!int.TryParse(match.Groups[1].Value, out int minutes)) continue;
                if (!double.TryParse(match.Groups[2].Value.Replace(':', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)) continue;
                TimeSpan start = TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds) + TimeSpan.FromMilliseconds(offsetMilliseconds);
                result.Add(new LyricLine(start < TimeSpan.Zero ? TimeSpan.Zero : start, text));
            }
        }

        return result.OrderBy(line => line.Start).ToList();
    }

    private static string? StripSyncedToPlain(string? synced)
    {
        if (string.IsNullOrWhiteSpace(synced)) return null;
        var lines = new List<string>();
        foreach (string raw in synced.Replace("\r", string.Empty).Split('\n'))
        {
            string text = Regex.Replace(raw, @"\[[^\]]*\]", " ");
            text = Regex.Replace(text, @"<\d{1,3}:\d{1,2}(?:[.:]\d{1,4})?>", " ");
            text = CollapseWhitespace(text);
            if (text.Length == 0) continue;
            lines.Add(text);
        }
        return lines.Count == 0 ? null : string.Join('\n', lines);
    }

    private static List<LyricLine> SpreadPlainLyrics(string? plainLyrics, TimeSpan duration)
    {
        if (string.IsNullOrWhiteSpace(plainLyrics)) return new List<LyricLine>();
        string[] lines = plainLyrics.Replace("\r", string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0) return new List<LyricLine>();
        double seconds = duration.TotalSeconds > 0 ? duration.TotalSeconds / lines.Length : 4;
        return lines.Select((text, index) => new LyricLine(TimeSpan.FromSeconds(index * seconds), text)).ToList();
    }

    /// <summary>Comparison key that ignores decorations which providers spell differently.</summary>
    private static string NormalizeForCompare(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        string lowered = value.ToLowerInvariant();
        lowered = Regex.Replace(lowered, @"[\(\[（【][^\)\]）】]*[\)\]）】]", " ");
        lowered = Regex.Replace(lowered, @"[^\p{L}\p{N}]+", string.Empty);
        return lowered;
    }

    private static bool ContainsCjk(string text)
    {
        foreach (char c in text)
        {
            if ((c >= '\u3040' && c <= '\u30ff') || (c >= '\u3400' && c <= '\u9fff') || (c >= '\uf900' && c <= '\ufaff')
                || (c >= '\uac00' && c <= '\ud7af')) return true;
        }
        return false;
    }

    private static bool IsRetriableStatus(HttpStatusCode status) =>
        status is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout
            or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    private static bool IsTransient(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException or JsonException or IOException;

    private static string BuildCacheKey(string title, string artist) =>
        // Duration is deliberately not part of the key: SMTC reports it a moment after the title,
        // and keying on it would throw away a freshly cached result on the next tick.
        $"{title.Trim().ToLowerInvariant()}\n{(artist ?? string.Empty).Trim().ToLowerInvariant()}";

    private void Remember(string key, LyricsResult lyrics)
    {
        lock (_cacheGate)
        {
            if (!_lyricsCache.ContainsKey(key))
            {
                _lyricsCache[key] = lyrics;
                _cacheOrder.Enqueue(key);
                while (_cacheOrder.Count > MaxCacheEntries)
                {
                    string oldestKey = _cacheOrder.Dequeue();
                    if (!_cacheOrder.Contains(oldestKey)) _lyricsCache.Remove(oldestKey);
                }
            }
            else
            {
                // Update the matching result without introducing a duplicate eviction entry.
                _lyricsCache[key] = lyrics;
            }
        }
        SaveCacheToDisk();
    }

    #region persistent cache

    private void LoadCacheFromDisk()
    {
        lock (_cacheGate)
        {
            if (_cacheFileLoaded) return;
            _cacheFileLoaded = true;

            try
            {
                if (!File.Exists(CacheFilePath)) return;
                List<CachedLyrics>? entries = JsonSerializer.Deserialize<List<CachedLyrics>>(File.ReadAllText(CacheFilePath), CacheJsonOptions);
                if (entries is null) return;

                // Oldest first so the eviction order below matches the original usage order.
                foreach (CachedLyrics entry in entries.OfType<CachedLyrics>().OrderBy(entry => entry.UsedAt))
                {
                    if (entry.Schema < 2) continue;
                    if (string.IsNullOrWhiteSpace(entry.Key) || entry.Lines is null || entry.Lines.Count == 0) continue;
                    if (_lyricsCache.ContainsKey(entry.Key)) continue;
                    var lines = entry.Lines
                        .Where(line => line is not null && !string.IsNullOrWhiteSpace(line.Text)
                            && double.IsFinite(line.Start) && line.Start >= 0 && line.Start <= TimeSpan.FromDays(1).TotalSeconds)
                        .Select(line => new LyricLine(TimeSpan.FromSeconds(line.Start), line.Text, line.Translation))
                        .OrderBy(line => line.Start)
                        .ToList();
                    // Old cache files may still hold credit-only payloads; they are not lyrics.
                    lines = WithoutCreditLines(lines);
                    if (lines.Count == 0) continue;
                    _lyricsCache[entry.Key] = new LyricsResult(entry.Title, entry.Artist, lines);
                    _cacheOrder.Enqueue(entry.Key);
                }

                while (_cacheOrder.Count > MaxCacheEntries)
                    _lyricsCache.Remove(_cacheOrder.Dequeue());
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "Lyrics:CacheLoad");
            }
        }
    }

    /// <summary>
    /// Coalesces cache writes, but snapshots only after the debounce interval. This avoids losing
    /// lyrics learned during a quick sequence of track changes and keeps dictionary access safe.
    /// </summary>
    private void SaveCacheToDisk()
    {
        Interlocked.Exchange(ref _cacheDirty, 1);
        if (Interlocked.CompareExchange(ref _cacheSaveQueued, 1, 0) != 0) return;

        _ = Task.Run(async () =>
        {
            try
            {
                do
                {
                    Interlocked.Exchange(ref _cacheDirty, 0);
                    await Task.Delay(500).ConfigureAwait(false);
                    List<CachedLyrics> snapshot;
                    lock (_cacheGate)
                    {
                        var orderedKeys = _cacheOrder
                            .Where(_lyricsCache.ContainsKey)
                            .Distinct(StringComparer.Ordinal)
                            .ToList();
                        foreach (string key in _lyricsCache.Keys)
                            if (!orderedKeys.Contains(key, StringComparer.Ordinal)) orderedKeys.Add(key);

                        long baseTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        snapshot = orderedKeys.Select((key, index) =>
                        {
                            LyricsResult value = _lyricsCache[key];
                            return new CachedLyrics
                            {
                                Key = key,
                                Title = value.Title,
                                Artist = value.Artist,
                                UsedAt = baseTimestamp + index,
                                Lines = value.Lines.Select(line => new CachedLine { Start = line.Start.TotalSeconds, Text = line.Text, Translation = line.Translation }).ToList()
                            };
                        }).ToList();
                    }

                    Directory.CreateDirectory(AppPaths.SettingsDirectory);
                    string temporary = CacheFilePath + ".tmp";
                    File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot, CacheJsonOptions));
                    File.Move(temporary, CacheFilePath, overwrite: true);
                }
                while (Interlocked.Exchange(ref _cacheDirty, 0) == 1);
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "Lyrics:CacheSave");
            }
            finally
            {
                Interlocked.Exchange(ref _cacheSaveQueued, 0);
                // Catch a write requested between the last dirty check and releasing the queue.
                if (Interlocked.Exchange(ref _cacheDirty, 0) == 1) SaveCacheToDisk();
            }
        });
    }

    private sealed class CachedLyrics
    {
        /// <summary>Bumped when the matching rules change, so old (possibly wrong) hits are dropped.</summary>
        public int Schema { get; set; } = 2;
        public string Key { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public long UsedAt { get; set; }
        public List<CachedLine> Lines { get; set; } = new();
    }

    private sealed class CachedLine
    {
        public double Start { get; set; }
        public string Text { get; set; } = string.Empty;
        public string? Translation { get; set; }
    }

    #endregion

    #endregion

    #region provider payloads

    private sealed class LrcLibResponse
    {
        [JsonPropertyName("trackName")] public string? TrackName { get; set; }
        [JsonPropertyName("artistName")] public string? ArtistName { get; set; }
        [JsonPropertyName("duration")] public double? Duration { get; set; }
        [JsonPropertyName("syncedLyrics")] public string? SyncedLyrics { get; set; }
        [JsonPropertyName("plainLyrics")] public string? PlainLyrics { get; set; }
        [JsonIgnore] public string? TranslationLyrics { get; set; }
    }

    private sealed class ItunesSearchResponse
    {
        [JsonPropertyName("resultCount")] public int ResultCount { get; set; }
        [JsonPropertyName("results")] public List<ItunesTrack>? Results { get; set; }
    }

    private sealed class ItunesTrack
    {
        [JsonPropertyName("wrapperType")] public string? WrapperType { get; set; }
        [JsonPropertyName("kind")] public string? Kind { get; set; }
        [JsonPropertyName("trackId")] public long TrackId { get; set; }
        [JsonPropertyName("collectionId")] public long CollectionId { get; set; }
        [JsonPropertyName("trackName")] public string? TrackName { get; set; }
        [JsonPropertyName("artistName")] public string? ArtistName { get; set; }
        [JsonPropertyName("collectionName")] public string? CollectionName { get; set; }
        [JsonPropertyName("trackNumber")] public int TrackNumber { get; set; }
        [JsonPropertyName("discNumber")] public int DiscNumber { get; set; }
        [JsonPropertyName("trackTimeMillis")] public double? TrackTimeMillis { get; set; }
    }

    private sealed class LyricsOvhResponse
    {
        [JsonPropertyName("lyrics")] public string? Lyrics { get; set; }
    }

    private sealed class NeteaseSearchResponse
    {
        [JsonPropertyName("code")] public int Code { get; set; }
        [JsonPropertyName("result")] public NeteaseSearchResult? Result { get; set; }
    }
    private sealed class NeteaseSearchResult { [JsonPropertyName("songs")] public List<NeteaseSong>? Songs { get; set; } }
    private sealed class NeteaseSong
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("duration")] public double? Duration { get; set; }
        [JsonPropertyName("dt")] public double? Dt { get; set; }
        [JsonPropertyName("artists")] public List<NeteaseArtist>? Artists { get; set; }
        [JsonPropertyName("ar")] public List<NeteaseArtist>? Ar { get; set; }
        public double? LengthMs => Duration ?? Dt;
        public List<NeteaseArtist>? ArtistList => Artists ?? Ar;
    }
    private sealed class NeteaseArtist { [JsonPropertyName("name")] public string? Name { get; set; } }
    private sealed class NeteaseLyricsResponse { [JsonPropertyName("lrc")] public NeteaseLrc? Lrc { get; set; } [JsonPropertyName("tlyric")] public NeteaseLrc? Tlyric { get; set; } }
    private sealed class NeteaseLrc { [JsonPropertyName("lyric")] public string? Lyric { get; set; } }

    #endregion
}
