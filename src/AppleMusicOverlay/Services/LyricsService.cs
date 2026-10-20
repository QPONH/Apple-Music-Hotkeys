using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AppleMusicOverlay.Services;

public sealed record LyricLine(TimeSpan Start, string Text);
public sealed record LyricsResult(string Title, string Artist, IReadOnlyList<LyricLine> Lines);

/// <summary>Outcome of one lyric lookup.</summary>
public enum LyricsLookupStatus
{
    /// <summary>Lyrics were found for the requested track.</summary>
    Found,
    /// <summary>Every provider answered, but none of them knew this track.</summary>
    NotFound,
    /// <summary>The lookup could not complete (network / rate limit), so a later retry may succeed.</summary>
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
/// </summary>
public sealed class LyricsService
{
    // One shared client: a client per lookup exhausts sockets and makes provider rate limits worse.
    private static readonly HttpClient Client = CreateClient();
    private const int MaxCacheEntries = 96;

    private readonly SmtcMediaSessionService _mediaService;
    private readonly Dictionary<string, LyricsResult> _lyricsCache = new(StringComparer.Ordinal);
    private readonly Queue<string> _cacheOrder = new();
    private readonly Dictionary<string, int> _lookupAttempts = new(StringComparer.Ordinal);

    public LyricsService(SmtcMediaSessionService mediaService) => _mediaService = mediaService;

    public Task<PlaybackSnapshot?> GetCurrentPlaybackAsync(CancellationToken token = default) => _mediaService.GetCurrentPlaybackAsync(token);

    public async Task<LyricsLookupResult> GetLyricsAsync(string title, string artist, TimeSpan duration, bool forceRefresh = false, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(title)) return LyricsLookupResult.NotFound;

        string cacheKey = BuildCacheKey(title, artist);
        if (forceRefresh)
        {
            // "重新查找歌词" must not be answered from the cache, otherwise a wrong match could
            // never be corrected by hand.
            _lyricsCache.Remove(cacheKey);
            _lookupAttempts.Remove(cacheKey);
        }
        else if (_lyricsCache.TryGetValue(cacheKey, out LyricsResult? cached))
        {
            return LyricsLookupResult.Found(cached);
        }

        // The overlay retries a missed lookup. Only the first attempt is exhaustive; retries use a
        // short candidate list so a track without lyrics cannot flood the providers.
        _lookupAttempts.TryGetValue(cacheKey, out int attempts);
        if (_lookupAttempts.Count > MaxCacheEntries) _lookupAttempts.Clear();
        _lookupAttempts[cacheKey] = attempts + 1;

        LyricsLookupResult lookup = await LookupAsync(title.Trim(), (artist ?? string.Empty).Trim(), duration, thorough: attempts == 0, token);
        if (lookup.Lyrics is not null)
        {
            Remember(cacheKey, lookup.Lyrics);
            _lookupAttempts.Remove(cacheKey);
        }
        return lookup;
    }

    private async Task<LyricsLookupResult> LookupAsync(string title, string artist, TimeSpan duration, bool thorough, CancellationToken token)
    {
        // LRCLIB is the primary source and is queried for every candidate first: it is fast and
        // returns synced lyrics. The fallbacks are slower, so they only see the closest candidates.
        bool primaryFailed = false;
        var candidates = BuildCandidates(title, artist).ToList();
        int titleCandidates = thorough ? candidates.Count : Math.Min(2, candidates.Count);

        for (int i = 0; i < titleCandidates; i++)
        {
            (string candidateTitle, string candidateArtist) = candidates[i];
            token.ThrowIfCancellationRequested();

            (LrcLibResponse? data, bool failed) = await TryLrclibAsync(candidateTitle, candidateArtist, duration, token);
            primaryFailed |= failed;
            if (data is not null) return BuildResult(candidateTitle, candidateArtist, data, duration);
        }

        if (!thorough) return primaryFailed ? LyricsLookupResult.Unavailable : LyricsLookupResult.NotFound;

        for (int i = 0; i < Math.Min(2, candidates.Count); i++)
        {
            (string candidateTitle, string candidateArtist) = candidates[i];
            (LrcLibResponse? data, _) = await TryNeteaseAsync(candidateTitle, candidateArtist, duration, token);
            if (data is not null) return BuildResult(candidateTitle, candidateArtist, data, duration);
        }

        (LrcLibResponse? ovhData, _) = await TryLyricsOvhAsync(candidates[0].Title, candidates[0].Artist, token);
        if (ovhData is not null) return BuildResult(candidates[0].Title, candidates[0].Artist, ovhData, duration);

        // Never cache an offline failure: report it as retriable so the overlay tries again.
        return primaryFailed ? LyricsLookupResult.Unavailable : LyricsLookupResult.NotFound;
    }

    private static LyricsLookupResult BuildResult(string title, string artist, LrcLibResponse data, TimeSpan duration)
    {
        List<LyricLine> lines = !string.IsNullOrWhiteSpace(data.SyncedLyrics)
            ? ParseLrc(data.SyncedLyrics)
            : SpreadPlainLyrics(data.PlainLyrics, duration);
        return lines.Count == 0 ? LyricsLookupResult.NotFound : LyricsLookupResult.Found(new LyricsResult(title, artist, lines));
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
            if (string.IsNullOrWhiteSpace(value)) return;
            string cleaned = CollapseWhitespace(value);
            cleaned = cleaned.Trim(' ', '-', '–', '—', '"', '\'', '\u201C', '\u201D', '\u2018', '\u2019', '《', '》');
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

    private static List<string> BuildArtistVariants(string artist)
    {
        var variants = new List<string>();
        void Add(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            string cleaned = CollapseWhitespace(value).Trim(' ', ',', '&', '/', '-', ';');
            if (cleaned.Length == 0) return;
            if (!variants.Contains(cleaned, StringComparer.OrdinalIgnoreCase)) variants.Add(cleaned);
        }

        Add(artist);

        // "A & B", "A, B", "A feat. B", "A x B": LRCLIB indexes the main credit only.
        // "/" is intentionally not a separator so band names such as AC/DC survive.
        string primary = Regex.Split(artist, @"\s*(?:,|&|;|\bfeat\.?|\bft\.?|\bfeaturing\b|\bwith\b|\bx\b|\bvs\.?\b)\s*", RegexOptions.IgnoreCase)[0];
        Add(primary);

        if (variants.Count == 0) variants.Add(string.Empty);
        return variants.Take(3).ToList();
    }

    private static string CollapseWhitespace(string value) => Regex.Replace(value, @"\s+", " ").Trim();

    #endregion

    #region LRCLIB

    /// <summary>
    /// GET with one quick retry. The reported machine regularly fails the first TLS handshake to
    /// LRCLIB ("The SSL connection could not be established") while the immediate retry succeeds,
    /// which is the main reason a song with lyrics occasionally showed "未找到歌词".
    /// </summary>
    private static async Task<HttpResponseMessage> GetAsync(string url, CancellationToken token) =>
        await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), token);

    private static async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> requestFactory, CancellationToken token)
    {
        try
        {
            using HttpRequestMessage request = requestFactory();
            return await Client.SendAsync(request, token);
        }
        catch (HttpRequestException) when (!token.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(350), token);
            using HttpRequestMessage retry = requestFactory();
            return await Client.SendAsync(retry, token);
        }
    }

    private static async Task<(LrcLibResponse? Data, bool Failed)> TryLrclibAsync(string title, string artist, TimeSpan duration, CancellationToken token)
    {
        try
        {
            bool failed = false;

            if (!string.IsNullOrWhiteSpace(artist))
            {
                string query = $"track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist)}";
                using HttpResponseMessage exact = await GetAsync($"https://lrclib.net/api/get?{query}", token);
                if (exact.IsSuccessStatusCode)
                {
                    LrcLibResponse? exactResult = await exact.Content.ReadFromJsonAsync<LrcLibResponse>(token);
                    if (HasLyrics(exactResult)) return (exactResult, false);
                }
                else if (IsRetriableStatus(exact.StatusCode))
                {
                    // 429 / 5xx: the exact match could easily exist, so remember the failure and
                    // still try the search endpoint below.
                    failed = true;
                }
            }

            (LrcLibResponse? found, bool searchFailed) = await SearchLrclibAsync(title, artist, duration, token);
            return (found, failed || searchFailed);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            CrashLog.Write(ex, "Lyrics:LRCLIB");
            return (null, true);
        }
    }

    /// <summary>
    /// Free-text / fielded search. Needed when the exact-match endpoint returns 404 because the
    /// stored title or artist differs slightly from what Apple Music reports.
    /// </summary>
    private static async Task<(LrcLibResponse? Data, bool Failed)> SearchLrclibAsync(string title, string artist, TimeSpan duration, CancellationToken token)
    {
        bool failed = false;
        var urls = new List<string>();
        if (!string.IsNullOrWhiteSpace(artist))
            urls.Add($"https://lrclib.net/api/search?track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist)}");
        urls.Add($"https://lrclib.net/api/search?q={Uri.EscapeDataString(string.IsNullOrWhiteSpace(artist) ? title : $"{title} {artist}")}");

        foreach (string url in urls)
        {
            using HttpResponseMessage response = await GetAsync(url, token);
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

            double score = 0;

            if (wantedSeconds > 0 && item.Duration is > 0)
            {
                double diff = Math.Abs(item.Duration.Value - wantedSeconds);
                // A different version (live / remix / extended) has different timing; playing it
                // would show lyrics that drift away from the music.
                if (diff > 12) continue;
                score += diff;
            }

            string itemTitle = NormalizeForCompare(item.TrackName);
            if (itemTitle.Length > 0 && wantedTitle.Length > 0)
            {
                if (itemTitle == wantedTitle) score -= 6;
                else if (itemTitle.Contains(wantedTitle) || wantedTitle.Contains(itemTitle)) score -= 2;
                else score += 4;
            }

            string itemArtist = NormalizeForCompare(item.ArtistName);
            if (itemArtist.Length > 0 && wantedArtist.Length > 0)
            {
                if (itemArtist == wantedArtist) score -= 3;
                else if (itemArtist.Contains(wantedArtist) || wantedArtist.Contains(itemArtist)) score -= 1;
                else score += 3;
            }

            if (score < bestScore)
            {
                bestScore = score;
                best = item;
            }
        }

        return best;
    }

    private static bool HasLyrics(LrcLibResponse? data) =>
        data is not null &&
        (!string.IsNullOrWhiteSpace(data.SyncedLyrics) || !string.IsNullOrWhiteSpace(data.PlainLyrics));

    #endregion

    #region NetEase / lyrics.ovh fallbacks

    private static async Task<(LrcLibResponse? Data, bool Failed)> TryNeteaseAsync(string title, string artist, TimeSpan duration, CancellationToken token)
    {
        try
        {
            string searchText = string.IsNullOrWhiteSpace(artist) ? title : $"{title} {artist}";
            string searchUrl = $"https://music.163.com/api/search/get/web?type=1&limit=15&s={Uri.EscapeDataString(searchText)}";
            using HttpResponseMessage search = await SendAsync(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, searchUrl);
                request.Headers.Referrer = new Uri("https://music.163.com/");
                return request;
            }, token);
            if (!search.IsSuccessStatusCode) return (null, IsRetriableStatus(search.StatusCode));

            NeteaseSearchResponse? results = await search.Content.ReadFromJsonAsync<NeteaseSearchResponse>(token);
            NeteaseSong? song = PickNeteaseSong(results?.Result?.Songs, title, artist, duration);
            if (song is null) return (null, false);

            string lyricsUrl = $"https://music.163.com/api/song/lyric?id={song.Id}&lv=-1&kv=-1&tv=-1";
            using HttpResponseMessage lyrics = await SendAsync(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, lyricsUrl);
                request.Headers.Referrer = new Uri("https://music.163.com/");
                return request;
            }, token);
            if (!lyrics.IsSuccessStatusCode) return (null, IsRetriableStatus(lyrics.StatusCode));

            NeteaseLyricsResponse? data = await lyrics.Content.ReadFromJsonAsync<NeteaseLyricsResponse>(token);
            string? synced = data?.Lrc?.Lyric;
            return (string.IsNullOrWhiteSpace(synced) ? null : new LrcLibResponse { SyncedLyrics = synced }, false);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            CrashLog.Write(ex, "Lyrics:Netease");
            return (null, true);
        }
    }

    private static NeteaseSong? PickNeteaseSong(List<NeteaseSong>? songs, string title, string artist, TimeSpan duration)
    {
        if (songs is null || songs.Count == 0) return null;

        string wantedArtist = NormalizeForCompare(artist);
        double wantedSeconds = duration.TotalSeconds;
        List<NeteaseSong> candidates = songs.Where(song => song.Id > 0).ToList();
        if (candidates.Count == 0) return null;

        bool AnyArtistMatch(NeteaseSong song) => song.Artists?.Any(a =>
        {
            string value = NormalizeForCompare(a.Name);
            return value.Length > 0 && wantedArtist.Length > 0 &&
                   (value == wantedArtist || value.Contains(wantedArtist) || wantedArtist.Contains(value));
        }) == true;

        // A title-only search can return covers and karaoke versions. When the artist is known,
        // only accept hits from that artist (or from an unknown result without artist data).
        if (wantedArtist.Length > 0 && candidates.Any(AnyArtistMatch))
            candidates = candidates.Where(AnyArtistMatch).ToList();

        return candidates
            .OrderBy(song => DurationDistance(song.Duration, wantedSeconds))
            .ThenBy(song => song.Id)
            .FirstOrDefault();
    }

    private static double DurationDistance(double? milliseconds, double wantedSeconds)
    {
        if (milliseconds is not > 0 || wantedSeconds <= 0) return 0;
        return Math.Abs(milliseconds.Value / 1000d - wantedSeconds);
    }

    private static async Task<(LrcLibResponse? Data, bool Failed)> TryLyricsOvhAsync(string title, string artist, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(artist)) return (null, false);

        try
        {
            using HttpResponseMessage response = await GetAsync($"https://api.lyrics.ovh/v1/{Uri.EscapeDataString(artist)}/{Uri.EscapeDataString(title)}", token);
            if (!response.IsSuccessStatusCode) return (null, IsRetriableStatus(response.StatusCode));

            LyricsOvhResponse? result = await response.Content.ReadFromJsonAsync<LyricsOvhResponse>(token);
            return (string.IsNullOrWhiteSpace(result?.Lyrics) ? null : new LrcLibResponse { PlainLyrics = result.Lyrics }, false);
        }
        catch (Exception ex) when (IsTransient(ex))
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

        var timestamp = new Regex(@"\[(\d{1,3}):(\d{1,2}(?:[.:]\d{1,3})?)\]");
        // Enhanced LRC keeps word timings inside the text: "<00:12.34>word".
        var wordTiming = new Regex(@"<\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?>");

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

    private static bool IsRetriableStatus(HttpStatusCode status) =>
        status is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout
            or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    private static bool IsTransient(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or System.IO.IOException;

    private static string BuildCacheKey(string title, string artist) =>
        // Duration is deliberately not part of the key: SMTC reports it a moment after the title,
        // and keying on it would throw away a freshly cached result on the next tick.
        $"{title.Trim().ToLowerInvariant()}\n{(artist ?? string.Empty).Trim().ToLowerInvariant()}";

    private void Remember(string key, LyricsResult lyrics)
    {
        if (_lyricsCache.ContainsKey(key)) return;
        _lyricsCache[key] = lyrics;
        _cacheOrder.Enqueue(key);
        while (_cacheOrder.Count > MaxCacheEntries)
            _lyricsCache.Remove(_cacheOrder.Dequeue());
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        // LRCLIB asks clients to identify themselves; generic agents are rate limited harder.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MusicFloat/14.26 (https://github.com/QPONH/Apple-Music-Hotkeys)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    #endregion

    #region provider payloads

    private sealed class LrcLibResponse
    {
        [JsonPropertyName("trackName")] public string? TrackName { get; set; }
        [JsonPropertyName("artistName")] public string? ArtistName { get; set; }
        [JsonPropertyName("duration")] public double? Duration { get; set; }
        [JsonPropertyName("syncedLyrics")] public string? SyncedLyrics { get; set; }
        [JsonPropertyName("plainLyrics")] public string? PlainLyrics { get; set; }
    }

    private sealed class LyricsOvhResponse
    {
        [JsonPropertyName("lyrics")] public string? Lyrics { get; set; }
    }

    private sealed class NeteaseSearchResponse { [JsonPropertyName("result")] public NeteaseSearchResult? Result { get; set; } }
    private sealed class NeteaseSearchResult { [JsonPropertyName("songs")] public List<NeteaseSong>? Songs { get; set; } }
    private sealed class NeteaseSong
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("duration")] public double? Duration { get; set; }
        [JsonPropertyName("artists")] public List<NeteaseArtist>? Artists { get; set; }
    }
    private sealed class NeteaseArtist { [JsonPropertyName("name")] public string? Name { get; set; } }
    private sealed class NeteaseLyricsResponse { [JsonPropertyName("lrc")] public NeteaseLrc? Lrc { get; set; } }
    private sealed class NeteaseLrc { [JsonPropertyName("lyric")] public string? Lyric { get; set; } }

    #endregion
}
