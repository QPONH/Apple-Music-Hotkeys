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

/// <summary>
/// One resolved lyric set. <paramref name="Placeholder"/> marks provider filler such as
/// 「纯音乐，请欣赏」: it is shown for a real instrumental track, but it must not stop the lookup
/// from trying a better catalog match when the provider answered for a different (instrumental) track.
/// </summary>
public sealed record LyricsResult(string Title, string Artist, IReadOnlyList<LyricLine> Lines, bool Placeholder = false);

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

    /// <summary>
    /// How long a found lyric waits for the parallel NetEase task to deliver its translation.
    /// NetEase needs a search plus one lyric request per candidate entry, so this has to be a
    /// meaningful share of the caller's per-attempt budget (4.5 s) or translations get lost.
    /// </summary>
    private static readonly TimeSpan TranslationGrace = TimeSpan.FromMilliseconds(2600);

    /// <summary>Localized title → catalog title pairs learned from the storefront (memory only).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Title, string Artist)> TitleAliases = new();

    /// <summary>Set after the storefront throttles us so the probe stops consuming the quota.</summary>
    private static DateTime _titleProbeBlockedUntilUtc = DateTime.MinValue;

    private static string TitleAliasKey(string title, string artist) =>
        NormalizeForCompare(artist) + "\n" + NormalizeForCompare(title);

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
            .Where(track => CatalogTextMatches(NormalizeForCompare(track.TrackName), wantedTitle)
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

    /// <summary>
    /// Apple Music shows localized titles ("void", "emptiness and catharsis") while the lyric
    /// providers index the original script (「空の箱」). Ask the iTunes storefronts for the same
    /// track and return its catalog title, which is then searched again.
    /// The storefront is asked with progressively broader terms because the album name is
    /// localized as well; the playing duration is what disambiguates the track.
    /// </summary>
    private static async Task<(string Title, string Artist)?> ResolveOriginalTitleAsync(
        string title, string artist, string album, TimeSpan duration, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(artist)) return null;
        if (DateTime.UtcNow < _titleProbeBlockedUntilUtc) return null;

        string wantedTitle = NormalizeForCompare(title);
        string wantedArtist = NormalizeForCompare(artist);
        string wantedAlbum = NormalizeForCompare(album);
        double wantedSeconds = duration.TotalSeconds;
        // Without a usable duration only an album/title match can tie a catalog entry to the track.
        if (wantedSeconds <= 0 && wantedAlbum.Length == 0 && string.IsNullOrWhiteSpace(title)) return null;

        // Two narrow terms at most, and the storefront requests are counted: the iTunes Search API
        // throttles per IP, and a throttled probe used to fail silently for every track afterwards.
        var terms = new List<string> { $"{artist} {title}", artist };
        var countries = new[] { "jp", "cn" };
        int requests = 0;
        foreach (string term in terms.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // A term that carries the localized title is specific enough to tolerate a bigger
            // duration drift (Apple Music and the storefront disagree on some releases).
            bool relaxedDuration = !string.Equals(term, artist, StringComparison.OrdinalIgnoreCase);
            foreach (string country in countries)
            {
                if (requests >= 3) return null;
                requests++;

                ItunesSearchResponse? payload;
                try
                {
                    string url = $"https://itunes.apple.com/search?term={Uri.EscapeDataString(term)}&entity=song&limit=50&country={country}";
                    using HttpResponseMessage response = await SendAsync(() => BrowserRequest(url), token);
                    if ((int)response.StatusCode == 403 || (int)response.StatusCode == 429)
                    {
                        // Throttled: stop asking for a while instead of burning the quota further.
                        _titleProbeBlockedUntilUtc = DateTime.UtcNow + TimeSpan.FromMinutes(3);
                        CrashLog.WriteNote("LyricsTitleProbe", $"throttled {(int)response.StatusCode}, pausing 3 min");
                        return null;
                    }
                    if (!response.IsSuccessStatusCode) continue;
                    payload = await response.Content.ReadFromJsonAsync<ItunesSearchResponse>(token);
                }
                catch (Exception ex) when (IsTransient(ex) && !token.IsCancellationRequested)
                {
                    // An unreachable storefront must not break the regular lookup.
                    continue;
                }
                if (payload?.Results is not { Count: > 0 }) continue;

                (string Title, string Artist)? resolved = PickLocalizedTitle(
                    payload.Results, artist, wantedTitle, wantedArtist, wantedAlbum, wantedSeconds, relaxedDuration);
                if (resolved is not null) return resolved;
            }
        }

        // The storefront may be unreachable or throttled. KuGou indexes the same catalog and is
        // reachable from China, so use it as the second opinion: search the artist and take the
        // song whose duration matches the playing track.
        return await ResolveTitleFromKugouAsync(title, artist, duration, token);
    }

    private static async Task<(string Title, string Artist)?> ResolveTitleFromKugouAsync(
        string title, string artist, TimeSpan duration, CancellationToken token)
    {
        if (duration <= TimeSpan.Zero) return null;
        try
        {
            string url = "https://songsearch.kugou.com/song_search_v2?page=1&pagesize=10&userid=-1&platform=WebFilter&filter=2&iscorrection=1&privilege_filter=0&keyword="
                + Uri.EscapeDataString(artist);
            using HttpResponseMessage response = await SendAsync(() => BrowserRequest(url), token);
            if (!response.IsSuccessStatusCode) return null;

            string body = await response.Content.ReadAsStringAsync(token);
            string wanted = NormalizeForCompare(title);
            KugouHit? best = null;
            double bestDifference = double.MaxValue;
            int nearMatches = 0;
            foreach (KugouHit hit in ReadKugouHits(body))
            {
                if (hit.DurationSeconds <= 0 || string.IsNullOrWhiteSpace(hit.Song)) continue;
                if (string.Equals(NormalizeForCompare(hit.Song), wanted, StringComparison.Ordinal)) continue;
                double difference = Math.Abs(hit.DurationSeconds - duration.TotalSeconds);
                if (difference > 1.5) continue;
                // Only a *unique* duration match identifies the track; otherwise a two-second
                // catalogue window would pick an equally long sibling song.
                if (++nearMatches > 1) return null;
                if (difference >= bestDifference) continue;
                bestDifference = difference;
                best = hit;
            }

            if (best is not { } match) return null;
            CrashLog.WriteNote("LyricsKugouTitle", $"{artist} -> {match.Song} | {match.Singer}");
            return (match.Song, string.IsNullOrWhiteSpace(match.Singer) ? artist : match.Singer);
        }
        catch (Exception ex) when (IsTransient(ex) && !token.IsCancellationRequested)
        {
            return null;
        }
    }

    private static (string Title, string Artist)? PickLocalizedTitle(
        List<ItunesTrack> tracks, string artist, string wantedTitle, string wantedArtist, string wantedAlbum,
        double wantedSeconds, bool relaxedDuration)
    {
        var ranked = new List<(ItunesTrack Track, double Difference, bool ArtistMatched)>();
        foreach (ItunesTrack track in tracks)
        {
            if (track.TrackTimeMillis is not > 0 || string.IsNullOrWhiteSpace(track.TrackName)) continue;
            // Karaoke / cover farm entries flood the storefront search and share the duration.
            if (LooksLikeCover(track)) continue;

            string candidateTitle = CleanMetadata(track.TrackName);
            if (candidateTitle.Length == 0) continue;
            if (string.Equals(NormalizeForCompare(candidateTitle), wantedTitle, StringComparison.Ordinal)) continue;

            bool artistMatched = CatalogTextMatches(NormalizeForCompare(track.ArtistName ?? string.Empty), wantedArtist);
            if (wantedSeconds <= 0)
            {
                // No usable duration. A specific term (artist + title/album) already ranks the right
                // entry first in the storefront response, so its ordering is trusted; a bare artist
                // term additionally needs the album to agree.
                bool albumMatched = CatalogTextMatches(NormalizeForCompare(track.CollectionName ?? string.Empty), wantedAlbum);
                if (!albumMatched && !relaxedDuration) continue;
                ranked.Add((track, 0, artistMatched));
                continue;
            }

            double difference = Math.Abs(track.TrackTimeMillis.Value / 1000d - wantedSeconds);
            // A romanized artist name never matches the native-script catalog credit, so an exact
            // duration match is the fallback signal; otherwise require the artist to agree.
            double tolerance = relaxedDuration ? 6.0 : artistMatched ? 2.5 : 1.5;
            if (difference > tolerance) continue;
            ranked.Add((track, difference, artistMatched));
        }

        foreach ((ItunesTrack track, _, _) in ranked
            .OrderByDescending(item => item.ArtistMatched)
            .ThenBy(item => item.Difference))
        {
            string candidateTitle = CleanMetadata(track.TrackName);
            string candidateArtist = CleanMetadata(track.ArtistName);
            return (candidateTitle, candidateArtist.Length > 0 ? candidateArtist : artist);
        }
        return null;
    }

    private static bool LooksLikeCover(ItunesTrack track)
    {
        string name = (track.TrackName ?? string.Empty) + " " + (track.ArtistName ?? string.Empty)
            + " " + (track.CollectionName ?? string.Empty);
        return name.Contains("karaoke", StringComparison.OrdinalIgnoreCase)
            || name.Contains("cover", StringComparison.OrdinalIgnoreCase)
            || name.Contains("tribute", StringComparison.OrdinalIgnoreCase)
            || name.Contains("originally performed", StringComparison.OrdinalIgnoreCase)
            || name.Contains("カラオケ", StringComparison.Ordinal)
            || name.Contains("カバー", StringComparison.Ordinal)
            || name.Contains("オルゴール", StringComparison.Ordinal)
            || name.Contains("インスト", StringComparison.Ordinal);
    }

    private static bool CatalogTextMatches(string candidate, string wanted) =>
        candidate.Length > 0 && wanted.Length > 0 && (candidate == wanted
            || (Math.Min(candidate.Length, wanted.Length) >= 4
                && (candidate.Contains(wanted, StringComparison.Ordinal) || wanted.Contains(candidate, StringComparison.Ordinal))));

    public async Task<LyricsLookupResult> GetLyricsAsync(string title, string artist, string? album, TimeSpan duration,
        bool forceRefresh = false, bool wantTranslation = false, CancellationToken token = default)
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
                // A cached hit without translations must not block the translation feature: fall
                // through to a fresh lookup so the NetEase translation can be attached.
                if (_lyricsCache.TryGetValue(cacheKey, out LyricsResult? cached)
                    && (!wantTranslation || HasTranslations(cached)))
                    return LyricsLookupResult.Found(cached);
            }
        }

        // Apple Music localizes titles ("void" for 「空の箱」). The catalog probe runs in parallel
        // with the lyric search so it does not have to fit into the caller's per-attempt budget;
        // it is cancelled as soon as a provider returns lyrics.
        string aliasKey = TitleAliasKey(title, artist);
        bool knownAlias = TitleAliases.TryGetValue(aliasKey, out (string Title, string Artist) alias);
        using var resolveCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        // Keep the probe inside a small budget of its own; when it loses the race the placeholder
        // result is kept, which is exactly the previous behaviour.
        resolveCancellation.CancelAfter(TimeSpan.FromMilliseconds(2800));
        Task<(string Title, string Artist)?> titleProbe = knownAlias
            ? Task.FromResult<(string Title, string Artist)?>(alias)
            : ResolveOriginalTitleAsync(title, artist, album, duration, resolveCancellation.Token);

        LyricsLookupResult lookup = await LookupAsync(title, artist, album, duration, wantTranslation, token);
        // Real lyrics end the catalog probe; a placeholder ("纯音乐，请欣赏") does not: it usually
        // means a provider matched an instrumental edition of a localized title.
        if (lookup.Lyrics is not null && !lookup.Lyrics.Placeholder) resolveCancellation.Cancel();
        else if (lookup.Lyrics is { Placeholder: true })
            CrashLog.WriteNote("LyricsPlaceholder", $"{title} | {artist} | {(int)duration.TotalSeconds}s");

        if (lookup.Lyrics is null || lookup.Lyrics.Placeholder)
        {
            (string Title, string Artist)? original = await AwaitTitleProbeAsync(titleProbe);
            if (original is { } resolved)
            {
                if (!knownAlias)
                {
                    if (TitleAliases.Count > 500) TitleAliases.Clear();
                    TitleAliases[aliasKey] = resolved;
                }
                CrashLog.WriteNote("LyricsTitleResolve", $"{title} | {artist} -> {resolved.Title} | {resolved.Artist}");
                LyricsLookupResult retried = await LookupAsync(resolved.Title, resolved.Artist, album, duration, wantTranslation, token);
                if (retried.Lyrics is not null && !retried.Lyrics.Placeholder) lookup = retried;
                else if (lookup.Lyrics is null && retried.Lyrics is not null) lookup = retried;
            }
            else if (lookup.Lyrics is null && lookup.Status == LyricsLookupStatus.NotFound)
            {
                CrashLog.WriteNote("LyricsTitleResolveMiss",
                    $"{title} | {artist} | album={album} | duration={(int)duration.TotalSeconds}s");
            }
        }

        resolveCancellation.Cancel();
        // A placeholder is not cached: the next play of the same track gets another chance to be
        // matched to its real (non-instrumental) catalog entry.
        if (lookup.Lyrics is not null && !lookup.Lyrics.Placeholder) Remember(cacheKey, lookup.Lyrics);
        return lookup;
    }

    /// <summary>Observes the probe task without letting its cancellation or a network fault escape.</summary>
    private static async Task<(string Title, string Artist)?> AwaitTitleProbeAsync(Task<(string Title, string Artist)?> probe)
    {
        try
        {
            return await probe;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "LyricsTitleProbe");
            return null;
        }
    }

    private static bool HasTranslations(LyricsResult lyrics) =>
        lyrics.Lines.Any(line => !string.IsNullOrWhiteSpace(line.Translation));

    private async Task<LyricsLookupResult> LookupAsync(string title, string artist, string album, TimeSpan duration,
        bool wantTranslation, CancellationToken token)
    {
        var candidates = BuildCandidates(title, artist).ToList();
        if (candidates.Count == 0) return LyricsLookupResult.NotFound;

        // Providers run concurrently, but the first acceptable result should cancel the others.
        // This avoids continuing several network searches after the overlay already has its lyrics.
        using var lookupCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        CancellationToken lookupToken = lookupCancellation.Token;
        Task<(LrcLibResponse? Data, bool Failed)> lrclibTask = TryLrclibCandidatesAsync(candidates, album, duration, lookupToken);
        Task<(LrcLibResponse? Data, bool Failed)> neteaseTask = TryNeteaseCandidatesAsync(candidates, duration, wantTranslation, lookupToken);
        Task<(LrcLibResponse? Data, bool Failed)> kugouTask = TryKugouAsync(title, artist, duration, lookupToken);
        Task<(LrcLibResponse? Data, bool Failed)> kuwoTask = TryKuwoAsync(candidates, duration, lookupToken);
        // QQ Music (search answers 500 without a signed request) and the gdstudio aggregator
        // (host unreachable) were verified dead on 2026-10-26, so they are no longer queried:
        // every request they cost was a wasted round trip on each track change.
        Task[] providerTasks = { lrclibTask, neteaseTask, kugouTask, kuwoTask };
        var fallbacks = new[] { neteaseTask, kugouTask, kuwoTask };
        // Only NetEase (tlyric) returns a timestamped translation among the live providers.
        var translationSources = new[] { neteaseTask };
        // The caller caps each attempt at 4.5 s, so the fallback grace period must be shorter
        // than that limit or the Chinese providers could be canceled before they are considered.
        Task grace = Task.Delay(TimeSpan.FromMilliseconds(1200), lookupToken);

        // When the caller wants a translation and the winning result has none, wait for the
        // providers that carry one instead of issuing a second search.
        async Task<LyricsLookupResult> FinalizeAsync(LyricsLookupResult result)
        {
            if (!wantTranslation || result.Lyrics is null || HasTranslations(result.Lyrics)) return result;
            try
            {
                if (translationSources.Any(task => !task.IsCompleted))
                {
                    using var graceCancellation = CancellationTokenSource.CreateLinkedTokenSource(lookupToken);
                    Task all = Task.WhenAll(translationSources);
                    await Task.WhenAny(all, Task.Delay(TranslationGrace, graceCancellation.Token));
                    graceCancellation.Cancel();
                }

                foreach (Task<(LrcLibResponse? Data, bool Failed)> task in translationSources)
                {
                    if (!task.IsCompleted || task.IsCanceled || task.IsFaulted) continue;
                    (LrcLibResponse? data, _) = await task;
                    if (string.IsNullOrWhiteSpace(data?.TranslationLyrics)) continue;
                    List<LyricLine> translated = ParseLrc(data.TranslationLyrics);
                    if (translated.Count == 0) continue;
                    return LyricsLookupResult.Found(new LyricsResult(result.Lyrics.Title, result.Lyrics.Artist,
                        AttachTranslations(result.Lyrics.Lines, translated)));
                }

                // Nothing carried a translation: record why, so this can be diagnosed from the log
                // instead of guessing which source answered.
                CrashLog.WriteNote("LyricsTranslationMiss",
                    $"{title} | {artist} | {(int)duration.TotalSeconds}s | {string.Join(", ", DescribeTranslationSources())}");
                return result;
            }
            catch (OperationCanceledException)
            {
                return result;
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "Lyrics:TranslationAttach");
                return result;
            }
        }

        string[] DescribeTranslationSources()
        {
            var states = new List<string>();
            foreach (Task<(LrcLibResponse? Data, bool Failed)> task in translationSources)
            {
                if (!task.IsCompleted)
                {
                    states.Add("pending");
                    continue;
                }
                if (task.IsFaulted)
                {
                    states.Add("failed");
                    continue;
                }
                if (task.IsCanceled)
                {
                    states.Add("canceled");
                    continue;
                }

                LrcLibResponse? data = task.Result.Data;
                if (data is null) states.Add("no-data");
                else if (data.TranslationLyrics is { Length: > 0 }) states.Add("has-translation");
                else states.Add($"no-translation(lyric={data.SyncedLyrics?.Length ?? 0})");
            }
            return states.ToArray();
        }

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
                    if (found is not null) return await FinalizeAsync(found);
                }

                if (lrclibTask.IsCompleted || grace.IsCompleted)
                {
                    foreach (Task<(LrcLibResponse? Data, bool Failed)> task in fallbacks)
                    {
                        if (!task.IsCompleted) continue;
                        LyricsLookupResult? found = TryBuild(title, artist, duration, await task);
                        if (found is not null) return await FinalizeAsync(found);
                    }
                }

                if (lrclibTask.IsCompleted && fallbacks.All(task => task.IsCompleted)) break;
            }

            LyricsLookupResult? lrclibFound = TryBuild(title, artist, duration, await lrclibTask);
            if (lrclibFound is not null) return await FinalizeAsync(lrclibFound);
            foreach (Task<(LrcLibResponse? Data, bool Failed)> task in fallbacks)
            {
                LyricsLookupResult? found = TryBuild(title, artist, duration, await task);
                if (found is not null) return await FinalizeAsync(found);
            }

            bool anyFailed = (await lrclibTask).Failed || (await neteaseTask).Failed || (await kugouTask).Failed;
            // lyrics.ovh is plain-text and often slow. Only spend that request on non-CJK tracks,
            // which are the ones it can actually answer, and only after the synced sources missed.
            if (!ContainsCjk(title) && !ContainsCjk(artist))
            {
                (LrcLibResponse? ovh, bool ovhFailed) = await TryLyricsOvhAsync(candidates[0].Title, candidates[0].Artist, lookupToken);
                anyFailed |= ovhFailed;
                LyricsLookupResult? ovhFound = TryBuild(candidates[0].Title, candidates[0].Artist, duration, (ovh, ovhFailed));
                if (ovhFound is not null) return await FinalizeAsync(ovhFound);
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
        return LyricsLookupResult.Found(new LyricsResult(title, artist, lines, IsPlaceholderLyric(lines)));
    }

    /// <summary>
    /// Provider filler such as 「纯音乐，请欣赏」. A genuinely instrumental track shows it, so it is
    /// kept — but it must not hide the real lyrics of the song the user is playing, which happens
    /// when a provider matched an instrumental/cover edition instead.
    /// </summary>
    private static bool IsPlaceholderLyric(IReadOnlyList<LyricLine> lines)
    {
        // The filler line can arrive wrapped in metadata lines ("出品人 : …", "纯音乐，请欣赏",
        // "推广 : …"), so every short line is inspected instead of only single-line payloads.
        if (lines.Count == 0 || lines.Count > 4) return false;
        foreach (LyricLine line in lines)
        {
            string text = line.Text.Trim();
            if (text.Length == 0 || text.Length > 16) continue;
            if (text.Contains("纯音乐", StringComparison.Ordinal)
                || text.Contains("純音樂", StringComparison.Ordinal)
                || text.Contains("請欣賞", StringComparison.Ordinal)
                || text.Contains("请欣赏", StringComparison.Ordinal)
                || text.Contains("暂无歌词", StringComparison.Ordinal)
                || text.Contains("暫無歌詞", StringComparison.Ordinal)
                || text.Contains("无歌词", StringComparison.Ordinal)
                || text.Contains("沒有歌詞", StringComparison.Ordinal)
                || text.Contains("Instrumental", StringComparison.OrdinalIgnoreCase)
                || text.Contains("No lyrics", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
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
        @"^\s*(?:作词|作詞|作曲|编曲|編曲|制作人?|製作人?|监制|監製|混音|母带|母帶|录音|錄音|和声|和聲|吉他|贝斯|貝斯|鼓|键盘|鍵盤|词|詞|曲|演唱|出品人?|发行|發行|统筹|統籌|企划|企劃|推广|宣傳|宣传|营销|營銷|策划|策劃|版权|版權|鸣谢|特別鳴謝|特别鸣谢|OP|SP|Lyrics?|Composer|Arranger|Producer|Written\s+by|Music\s+by|Mixed\s+by|Mastered\s+by)\s*[:：]",
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
        if (bounded[^1] != string.Empty) bounded.Add(string.Empty);
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
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MusicFloat/14.47 (https://github.com/QPONH/Apple-Music-Hotkeys)");
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
        IReadOnlyList<(string Title, string Artist)> candidates, TimeSpan duration, bool preferTranslation,
        CancellationToken token)
    {
        try
        {
            bool failed = false;
            int tried = 0;
            foreach ((string title, string artist) in candidates)
            {
                if (tried++ >= 2) break;
                (LrcLibResponse? data, bool callFailed) = await TryNeteaseAsync(title, artist, duration, preferTranslation, token);
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

    private static async Task<(LrcLibResponse? Data, bool Failed)> TryNeteaseAsync(
        string title, string artist, TimeSpan duration, bool preferTranslation, CancellationToken token)
    {
        bool failed = false;
        // "(feat. 初音ミク)", "(Live)" and "- Single" decorations make the NetEase search miss the
        // very track it indexes, so the core title/artist is searched first, then the full text.
        foreach (string searchText in BuildNeteaseSearchTexts(title, artist))
        {
            NeteaseSearchResponse? results = null;
            foreach (string endpoint in NeteaseSearchEndpoints)
            {
                using HttpResponseMessage search = await SendAsync(
                    () => NetEaseRequest($"{endpoint}{Uri.EscapeDataString(searchText)}"), token);
                if (!search.IsSuccessStatusCode)
                {
                    failed |= IsRetriableStatus(search.StatusCode) || search.StatusCode == HttpStatusCode.Forbidden;
                    continue;
                }

                results = await search.Content.ReadFromJsonAsync<NeteaseSearchResponse>(token);
                if (results?.Code is > 0 and not 200) failed = true;
                if (results?.Result?.Songs is { Count: > 0 }) break;
            }

            // Several catalogue entries can share title/artist/duration; only some carry a lyric
            // (and a translation), so walk the ranked hits until one answers with content. When a
            // translation is wanted, an entry that has one is preferred over the first hit with
            // lyrics only (duplicates and instrumental editions are common).
            LrcLibResponse? withoutTranslation = null;
            foreach (NeteaseSong song in RankNeteaseSongs(results?.Result?.Songs, title, artist, duration))
            {
                // The lyric endpoint is mirrored on several NetEase hosts. A single blocked host (the
                // failure seen in the field is a TLS reset on music.163.com) must not cost the lyrics.
                NeteaseLyricsResponse? data = null;
                foreach (string lyricEndpoint in NeteaseLyricEndpoints)
                {
                    string lyricsUrl = $"{lyricEndpoint}{song.Id}&lv=-1&kv=-1&tv=-1";
                    using HttpResponseMessage lyrics = await SendAsync(() => NetEaseRequest(lyricsUrl), token);
                    if (!lyrics.IsSuccessStatusCode)
                    {
                        failed |= IsRetriableStatus(lyrics.StatusCode) || lyrics.StatusCode == HttpStatusCode.Forbidden;
                        continue;
                    }

                    data = await lyrics.Content.ReadFromJsonAsync<NeteaseLyricsResponse>(token);
                    if (!string.IsNullOrWhiteSpace(data?.Lrc?.Lyric)) break;
                }

                string? synced = data?.Lrc?.Lyric;
                if (string.IsNullOrWhiteSpace(synced)) continue;

                var response = new LrcLibResponse
                {
                    TrackName = title,
                    ArtistName = artist,
                    SyncedLyrics = synced,
                    TranslationLyrics = data?.Tlyric?.Lyric
                };
                if (!preferTranslation || !string.IsNullOrWhiteSpace(response.TranslationLyrics))
                    return (response, false);
                withoutTranslation ??= response;
            }
            if (withoutTranslation is not null) return (withoutTranslation, false);
        }
        return (null, failed);
    }

    private static readonly string[] NeteaseSearchEndpoints =
    {
        "https://music.163.com/api/cloudsearch/pc?type=1&limit=12&s=",
        "https://interface.music.163.com/api/cloudsearch/pc?type=1&limit=12&s=",
        "https://interface3.music.163.com/api/cloudsearch/pc?type=1&limit=12&s=",
        "https://music.163.com/api/search/get/web?type=1&limit=12&s="
    };

    private static readonly string[] NeteaseLyricEndpoints =
    {
        "https://music.163.com/api/song/lyric?id=",
        "https://interface.music.163.com/api/song/lyric?id=",
        "https://interface3.music.163.com/api/song/lyric?id="
    };

    private static List<string> BuildNeteaseSearchTexts(string title, string artist)
    {
        string coreTitle = Regex.Replace(title, @"\s*[\(\[（【].*?[\)\]）】]\s*", " ").Trim();
        if (coreTitle.Length == 0) coreTitle = title;
        string coreArtist = Regex.Split(artist,
            @"\s*(?:,|&|;|、|／|\+|\bfeat\.?|\bft\.?|\bfeaturing\b|\bwith\b|\bx\b|\bvs\.?\b)\s*",
            RegexOptions.IgnoreCase)[0].Trim();

        var texts = new List<string>();
        void Add(string value)
        {
            value = value.Trim();
            if (value.Length == 0) return;
            if (!texts.Contains(value, StringComparer.OrdinalIgnoreCase)) texts.Add(value);
        }

        Add(string.IsNullOrWhiteSpace(coreArtist) ? coreTitle : $"{coreTitle} {coreArtist}");
        Add(string.IsNullOrWhiteSpace(artist) ? title : $"{title} {artist}");
        return texts;
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

    /// <summary>
    /// Ranks the NetEase hits instead of returning only the best one: the catalogue often holds
    /// several entries for the same track (duplicates, re-releases) and only some of them carry a
    /// lyric, so the caller walks the list until it finds one with content and a translation.
    /// </summary>
    private static List<NeteaseSong> RankNeteaseSongs(List<NeteaseSong>? songs, string title, string artist, TimeSpan duration)
    {
        var ranked = new List<(NeteaseSong Song, double Score)>();
        if (songs is null || songs.Count == 0) return new List<NeteaseSong>();

        string wantedTitle = NormalizeForCompare(title);
        string wantedArtist = NormalizeForCompare(artist);
        double wantedSeconds = duration.TotalSeconds;

        foreach (NeteaseSong song in songs)
        {
            if (song.Id <= 0) continue;

            string itemTitle = NormalizeForCompare(song.Name);
            bool artistMatch = song.ArtistList?.Any(a => MatchesText(NormalizeForCompare(a.Name), wantedArtist)) == true;
            double? durationDiff = wantedSeconds >= 5 && song.LengthMs is > 0
                ? Math.Abs(song.LengthMs.Value / 1000d - wantedSeconds) : null;
            double? score = ScoreCandidate(wantedTitle, itemTitle, artistMatch, durationDiff);
            if (score is not double candidateScore) continue;
            ranked.Add((song, candidateScore));
        }

        return ranked.OrderBy(item => item.Score).Take(4).Select(item => item.Song).ToList();
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

            bool failed = false;
            string body = await search.Content.ReadAsStringAsync(token);
            List<KugouHit> hits = ReadKugouHits(body);

            foreach (KugouHit hit in RankKugouHits(hits, title, searchArtist, duration))
            {
                int durationMs = hit.DurationSeconds > 0
                    ? (int)Math.Round(hit.DurationSeconds * 1000)
                    : (int)Math.Round(Math.Max(0, duration.TotalMilliseconds));
                string lyricKeyword = string.IsNullOrWhiteSpace(hit.Singer) ? hit.Song : $"{hit.Singer} - {hit.Song}";
                string lyricSearch = $"https://lyrics.kugou.com/search?ver=1&man=yes&client=pc&keyword={Uri.EscapeDataString(lyricKeyword)}&duration={durationMs}&hash={Uri.EscapeDataString(hit.Hash)}";
                using HttpResponseMessage found = await SendAsync(() => BrowserRequest(lyricSearch), token);
                if (!found.IsSuccessStatusCode)
                {
                    failed |= IsRetriableStatus(found.StatusCode);
                    continue;
                }

                using JsonDocument lyricDoc = JsonDocument.Parse(await found.Content.ReadAsStringAsync(token));
                if (!lyricDoc.RootElement.TryGetProperty("candidates", out JsonElement candidates) || candidates.ValueKind != JsonValueKind.Array)
                    continue;

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
                        return (new LrcLibResponse { TrackName = hit.Song, ArtistName = hit.Singer, SyncedLyrics = text }, false);
                }
            }

            return (null, failed);
        }
        catch (Exception ex) when (IsTransient(ex) && !token.IsCancellationRequested)
        {
            CrashLog.Write(ex, "Lyrics:Kugou");
            return (null, true);
        }
    }

    /// <summary>
    /// Kuwo (酷我音乐): reachable from mainland China without a proxy, and it indexes both the
    /// Chinese and the international catalogue. It carries synced lyrics only (no translation).
    /// </summary>
    private static async Task<(LrcLibResponse? Data, bool Failed)> TryKuwoAsync(
        IReadOnlyList<(string Title, string Artist)> candidates, TimeSpan duration, CancellationToken token)
    {
        try
        {
            bool failed = false;
            int tried = 0;
            foreach ((string title, string artist) in candidates)
            {
                // One search per provider keeps five concurrent sources from flooding the network
                // (and the UI) on every track change.
                if (tried++ >= 1) break;
                (LrcLibResponse? data, bool callFailed) = await TryKuwoCandidateAsync(title, artist, duration, token);
                failed |= callFailed;
                if (data is not null) return (data, false);
            }
            return (null, failed);
        }
        catch (Exception ex) when (IsTransient(ex) && !token.IsCancellationRequested)
        {
            CrashLog.Write(ex, "Lyrics:Kuwo");
            return (null, true);
        }
    }

    private static async Task<(LrcLibResponse? Data, bool Failed)> TryKuwoCandidateAsync(
        string title, string artist, TimeSpan duration, CancellationToken token)
    {
        string keyword = string.IsNullOrWhiteSpace(artist) ? title : $"{title} {artist}";
        string searchUrl = "https://search.kuwo.cn/r.s?all=" + Uri.EscapeDataString(keyword)
            + "&ft=music&client=kt&pn=0&rn=10&rformat=json&encoding=utf8";
        using HttpResponseMessage search = await SendAsync(() => BrowserRequest(searchUrl), token);
        if (!search.IsSuccessStatusCode) return (null, IsRetriableStatus(search.StatusCode));

        string body = await search.Content.ReadAsStringAsync(token);
        // The Kuwo payload is a large JS-ish literal; scanning it must never run on the UI thread.
        List<long> musicIds = await Task.Run(() => RankKuwoSongIds(body, title, artist, duration), token);
        foreach (long musicId in musicIds)
        {
            string lyricUrl = $"https://m.kuwo.cn/newh5/singles/songinfoandlrc?musicId={musicId}&httpsStatus=1";
            using HttpResponseMessage lyric = await SendAsync(() => BrowserRequest(lyricUrl), token);
            if (!lyric.IsSuccessStatusCode) continue;

            string? lrc = ReadKuwoLyric(await lyric.Content.ReadAsStringAsync(token));
            if (string.IsNullOrWhiteSpace(lrc)) continue;
            return (new LrcLibResponse { TrackName = title, ArtistName = artist, SyncedLyrics = lrc }, false);
        }
        return (null, false);
    }

    /// <summary>
    /// Reads the Kuwo search payload (a JS-ish literal) and returns the best music ids in ranked
    /// order: the top hit can be an edition without a lyric, so a couple of alternatives are kept.
    /// </summary>
    private static List<long> RankKuwoSongIds(string json, string title, string artist, TimeSpan duration)
    {
        string wantedTitle = NormalizeForCompare(title);
        string wantedArtist = NormalizeForCompare(artist);
        double wantedSeconds = duration.TotalSeconds;

        var ranked = new List<(long Id, double Score)>();
        // Kuwo answers with a JS-ish literal (single quotes, &nbsp; entities, hundreds of KB). A
        // nested-quantifier regex over that text backtracked for seconds and froze the overlay on
        // every track change, so the objects are sliced out with a single linear scan instead.
        foreach (string item in ScanJsonLikeObjects(json))
        {
            string name = HtmlDecode(JsonLikeValue(item, "SONGNAME", "NAME"));
            string singer = HtmlDecode(JsonLikeValue(item, "ARTIST"));
            string rid = JsonLikeValue(item, "MUSICRID");
            double seconds = double.TryParse(JsonLikeValue(item, "DURATION"),
                NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : 0;
            if (name.Length == 0 || !rid.StartsWith("MUSIC_", StringComparison.OrdinalIgnoreCase)) continue;
            if (!long.TryParse(rid["MUSIC_".Length..], out long id)) continue;

            double? difference = wantedSeconds >= 5 && seconds > 0 ? Math.Abs(seconds - wantedSeconds) : null;
            double? score = ScoreCandidate(wantedTitle, wantedArtist, NormalizeForCompare(name),
                NormalizeForCompare(singer), difference);
            if (score is not double value) continue;
            ranked.Add((id, value));
        }

        return ranked.OrderBy(item => item.Score).Take(2).Select(item => item.Id).ToList();
    }

    /// <summary>
    /// Slices the song objects out of Kuwo's JS-ish literal (single quotes, hundreds of KB) in one
    /// linear pass. A nested-quantifier regex over that text backtracked for seconds and froze the
    /// overlay on every track change. String literals are skipped so braces in text cannot break the
    /// nesting count.
    /// </summary>
    private static IEnumerable<string> ScanJsonLikeObjects(string text)
    {
        int searchFrom = text.IndexOf("abslist", StringComparison.OrdinalIgnoreCase);
        int index = searchFrom < 0 ? 0 : searchFrom;

        while (index < text.Length)
        {
            if (text[index] != '{')
            {
                index++;
                continue;
            }

            int depth = 0;
            int end = -1;
            for (int cursor = index; cursor < text.Length; cursor++)
            {
                char current = text[cursor];
                if (current is '\'' or '"')
                {
                    char quote = current;
                    cursor++;
                    while (cursor < text.Length)
                    {
                        if (text[cursor] == '\\') cursor++;
                        else if (text[cursor] == quote) break;
                        cursor++;
                    }
                    if (cursor >= text.Length) yield break;
                    continue;
                }

                if (current == '{')
                {
                    depth++;
                }
                else if (current == '}' && --depth == 0)
                {
                    end = cursor;
                    break;
                }
            }

            if (end < 0) yield break;
            yield return text[index..(end + 1)];
            index = end + 1;
        }
    }

    /// <summary>Pulls a plain value out of the loosely-quoted Kuwo literal (no HTML escaping).</summary>
    private static string JsonLikeValue(string source, params string[] names)
    {
        foreach (string name in names)
        {
            Match match = Regex.Match(source,
                "['\"]" + Regex.Escape(name) + "['\"]\\s*:\\s*(?:\"([^\"]*)\"|'([^']*)'|([^,}]*))");
            if (!match.Success) continue;
            string value = match.Groups[1].Success ? match.Groups[1].Value
                : match.Groups[2].Success ? match.Groups[2].Value
                : match.Groups[3].Value;
            value = value.Trim().Trim('\'', '"');
            if (value.Length > 0) return value;
        }
        return string.Empty;
    }

    private static string HtmlDecode(string value) =>
        System.Net.WebUtility.HtmlDecode(value.Replace("\\u0026", "&", StringComparison.Ordinal)).Trim();

    /// <summary>Turns Kuwo's line list into an LRC document so the normal parser can be reused.</summary>
    private static string? ReadKuwoLyric(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("data", out JsonElement data)) return null;
        if (!data.TryGetProperty("lrclist", out JsonElement lines) || lines.ValueKind != JsonValueKind.Array) return null;

        var builder = new System.Text.StringBuilder();
        foreach (JsonElement line in lines.EnumerateArray())
        {
            string text = line.TryGetProperty("lineLyric", out JsonElement textElement) ? textElement.GetString() ?? string.Empty : string.Empty;
            string time = line.TryGetProperty("time", out JsonElement timeElement) ? timeElement.ToString() : string.Empty;
            if (text.Length == 0 || !double.TryParse(time, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)) continue;
            builder.Append('[').Append(TimeSpan.FromSeconds(seconds).ToString(@"mm\:ss\.ff", CultureInfo.InvariantCulture)).Append(']')
                .Append(text).Append('\n');
        }
        return builder.Length == 0 ? null : builder.ToString();
    }

    /// <summary>
    /// QQ Music (QQ 音乐): the only other mainland-reachable provider that returns a timestamped
    /// translation (<c>trans</c>) next to the original lyric. Both endpoints require a y.qq.com
    /// referer, otherwise they answer "禁止跨域访问".
    /// </summary>
    private static async Task<(LrcLibResponse? Data, bool Failed)> TryQQMusicAsync(
        IReadOnlyList<(string Title, string Artist)> candidates, TimeSpan duration, CancellationToken token)
    {
        try
        {
            bool failed = false;
            int tried = 0;
            foreach ((string title, string artist) in candidates)
            {
                if (tried++ >= 1) break;
                (LrcLibResponse? data, bool callFailed) = await TryQQMusicCandidateAsync(title, artist, duration, token);
                failed |= callFailed;
                if (data is not null) return (data, false);
            }
            return (null, failed);
        }
        catch (Exception ex) when (IsTransient(ex) && !token.IsCancellationRequested)
        {
            CrashLog.Write(ex, "Lyrics:QQMusic");
            return (null, true);
        }
    }

    private static async Task<(LrcLibResponse? Data, bool Failed)> TryQQMusicCandidateAsync(
        string title, string artist, TimeSpan duration, CancellationToken token)
    {
        string keyword = string.IsNullOrWhiteSpace(artist) ? title : $"{title} {artist}";
        string searchUrl = "https://c.y.qq.com/soso/fcgi-bin/client_search_cp?format=json&cr=1&gtk=5381&t=0&aggr=1&lossless=0&flag_qc=0&p=1&n=10&w="
            + Uri.EscapeDataString(keyword);
        using HttpResponseMessage search = await SendAsync(() => QQMusicRequest(searchUrl), token);
        if (!search.IsSuccessStatusCode) return (null, IsRetriableStatus(search.StatusCode));

        QqSearchResponse? payload = await search.Content.ReadFromJsonAsync<QqSearchResponse>(token);
        string? songMid = PickQqSongMid(payload, title, artist, duration);
        if (songMid is null)
        {
            CrashLog.WriteNote("LyricsQQ", $"no match for {title} | {artist} ({payload?.Data?.Song?.List?.Count ?? 0} candidates)");
            return (null, false);
        }

        // Two documented spellings of the same lyric endpoint; the first is what the web player uses.
        // The extra parameters mirror the web player request, which the endpoint validates.
        const string webArgs = "&format=json&nobase64=1&gtk=5381&loginUin=0&hostUin=0&platform=yqq&needNewCode=0";
        string[] lyricUrls =
        {
            $"https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg?songmid={Uri.EscapeDataString(songMid)}{webArgs}",
            $"https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric.fcg?songmid={Uri.EscapeDataString(songMid)}{webArgs}"
        };
        foreach (string lyricUrl in lyricUrls)
        {
            using HttpResponseMessage lyric = await SendAsync(() => QQMusicRequest(lyricUrl), token);
            if (!lyric.IsSuccessStatusCode)
            {
                CrashLog.WriteNote("LyricsQQ", $"lyric http {(int)lyric.StatusCode} for {title}");
                continue;
            }

            QqLyricResponse? data = await lyric.Content.ReadFromJsonAsync<QqLyricResponse>(token);
            string? synced = HtmlDecode(data?.Lyric ?? string.Empty);
            if (string.IsNullOrWhiteSpace(synced))
            {
                CrashLog.WriteNote("LyricsQQ", $"no lyric payload for {title} (mid={songMid})");
                continue;
            }

            string translation = HtmlDecode(data?.Trans ?? string.Empty);
            if (translation.Length == 0)
                CrashLog.WriteNote("LyricsQQ", $"lyric without translation for {title} (mid={songMid})");
            return (new LrcLibResponse
            {
                TrackName = title,
                ArtistName = artist,
                SyncedLyrics = synced,
                TranslationLyrics = translation.Length == 0 ? null : translation
            }, false);
        }
        return (null, false);
    }

    private static string? PickQqSongMid(QqSearchResponse? payload, string title, string artist, TimeSpan duration)
    {
        List<QqSong>? songs = payload?.Data?.Song?.List;
        if (songs is null || songs.Count == 0) return null;

        string wantedTitle = NormalizeForCompare(title);
        string wantedArtist = NormalizeForCompare(artist);
        double wantedSeconds = duration.TotalSeconds;

        double bestScore = double.MaxValue;
        string? bestMid = null;
        foreach (QqSong song in songs)
        {
            if (string.IsNullOrWhiteSpace(song.SongMid) || string.IsNullOrWhiteSpace(song.SongName)) continue;
            bool artistMatch = song.Singers?.Any(s => MatchesText(NormalizeForCompare(s.Name ?? string.Empty), wantedArtist)) == true;
            double? difference = wantedSeconds >= 5 && song.Interval > 0 ? Math.Abs(song.Interval - wantedSeconds) : null;
            double? score = ScoreCandidate(wantedTitle, NormalizeForCompare(song.SongName), artistMatch, difference);
            if (score is not double value || value >= bestScore) continue;
            bestScore = value;
            bestMid = song.SongMid;
        }
        return bestMid;
    }

    private static HttpRequestMessage QQMusicRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
        request.Headers.Referrer = new Uri("https://y.qq.com/");
        return request;
    }

    /// <summary>
    /// Aggregator endpoint that proxies the NetEase catalogue (search + lyric with translation) and
    /// is reachable from mainland China even when <c>music.163.com</c> itself is blocked or throttled.
    /// It is the only source that reliably returns a translation for international tracks, so it is
    /// queried whenever the caller wants one.
    /// </summary>
    private static async Task<(LrcLibResponse? Data, bool Failed)> TryAggregatorAsync(
        IReadOnlyList<(string Title, string Artist)> candidates, TimeSpan duration, CancellationToken token)
    {
        try
        {
            bool failed = false;
            int tried = 0;
            foreach ((string title, string artist) in candidates)
            {
                if (tried++ >= 1) break;
                (LrcLibResponse? data, bool callFailed) = await TryAggregatorCandidateAsync(title, artist, duration, token);
                failed |= callFailed;
                if (data is not null) return (data, false);
            }
            return (null, failed);
        }
        catch (Exception ex) when (IsTransient(ex) && !token.IsCancellationRequested)
        {
            CrashLog.Write(ex, "Lyrics:Aggregator");
            return (null, true);
        }
    }

    private static async Task<(LrcLibResponse? Data, bool Failed)> TryAggregatorCandidateAsync(
        string title, string artist, TimeSpan duration, CancellationToken token)
    {
        string keyword = string.IsNullOrWhiteSpace(artist) ? title : $"{title} {artist}";
        string searchUrl = "https://music-api.gdstudio.xyz/api.php?types=search&source=netease&count=10&name="
            + Uri.EscapeDataString(keyword);
        using HttpResponseMessage search = await SendAsync(() => BrowserRequest(searchUrl), token);
        if (!search.IsSuccessStatusCode) return (null, IsRetriableStatus(search.StatusCode));

        List<AggregatorSong>? songs = await search.Content.ReadFromJsonAsync<List<AggregatorSong>>(token);
        string? lyricId = PickAggregatorSong(songs, title, artist);
        if (lyricId is null)
        {
            CrashLog.WriteNote("LyricsAggregator", $"no match for {title} | {artist} ({songs?.Count ?? 0} candidates)");
            return (null, false);
        }

        string lyricUrl = "https://music-api.gdstudio.xyz/api.php?types=lyric&source=netease&id="
            + Uri.EscapeDataString(lyricId);
        using HttpResponseMessage lyric = await SendAsync(() => BrowserRequest(lyricUrl), token);
        if (!lyric.IsSuccessStatusCode) return (null, IsRetriableStatus(lyric.StatusCode));

        AggregatorLyric? data = await lyric.Content.ReadFromJsonAsync<AggregatorLyric>(token);
        string? synced = data?.Lyric;
        if (string.IsNullOrWhiteSpace(synced)) return (null, false);
        string? translation = string.IsNullOrWhiteSpace(data?.TLyric) ? null : data.TLyric;
        return (new LrcLibResponse
        {
            TrackName = title,
            ArtistName = artist,
            SyncedLyrics = synced,
            TranslationLyrics = translation
        }, false);
    }

    private static string? PickAggregatorSong(List<AggregatorSong>? songs, string title, string artist)
    {
        if (songs is null || songs.Count == 0) return null;

        string wantedTitle = NormalizeForCompare(title);
        string wantedArtist = NormalizeForCompare(artist);
        double bestScore = double.MaxValue;
        string? bestId = null;
        foreach (AggregatorSong song in songs)
        {
            string id = song.LyricId ?? song.Id ?? string.Empty;
            if (id.Length == 0 || string.IsNullOrWhiteSpace(song.Name)) continue;

            string itemTitle = NormalizeForCompare(song.Name);
            bool titleExact = itemTitle == wantedTitle;
            if (!titleExact && !MatchesText(itemTitle, wantedTitle)) continue;
            bool artistMatch = wantedArtist.Length == 0
                || song.Artist?.Any(name => MatchesText(NormalizeForCompare(name ?? string.Empty), wantedArtist)) == true;
            // No duration in this payload, so an artist match is what makes the pick trustworthy.
            if (!artistMatch) continue;

            double score = titleExact ? -8 : -3;
            if (score >= bestScore) continue;
            bestScore = score;
            bestId = id;
        }
        return bestId;
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

    /// <summary>
    /// Ranked KuGou hits instead of only the best one: the winner can be an entry whose lyric
    /// endpoint returns nothing (instrumental editions, region-locked releases), so the caller
    /// walks a couple of alternatives before giving up.
    /// </summary>
    private static List<KugouHit> RankKugouHits(List<KugouHit> hits, string title, string artist, TimeSpan duration)
    {
        var ranked = new List<(KugouHit Hit, double Score)>();
        if (hits.Count == 0) return new List<KugouHit>();

        string wantedTitle = NormalizeForCompare(title);
        string wantedArtist = NormalizeForCompare(artist);
        double wantedSeconds = duration.TotalSeconds;

        foreach (KugouHit hit in hits)
        {
            string itemTitle = NormalizeForCompare(hit.Song);
            string itemArtist = NormalizeForCompare(hit.Singer);
            double? durationDiff = wantedSeconds >= 5 && hit.DurationSeconds > 0
                ? Math.Abs(hit.DurationSeconds - wantedSeconds) : null;
            double? score = ScoreCandidate(wantedTitle, wantedArtist, itemTitle, itemArtist, durationDiff);
            if (score is not double candidateScore) continue;
            ranked.Add((hit, candidateScore));
        }

        return ranked.OrderBy(item => item.Score).Take(2).Select(item => item.Hit).ToList();
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
                    _lyricsCache[entry.Key] = new LyricsResult(entry.Title, entry.Artist, lines, IsPlaceholderLyric(lines));
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
    /// Drops every cached lyric (memory and lyrics-cache.json) so a wrong hit — for example a
    /// localized title that was matched to an instrumental edition — can be resolved again.
    /// </summary>
    public void ClearCache()
    {
        lock (_cacheGate)
        {
            _lyricsCache.Clear();
            _cacheOrder.Clear();
        }

        // Stop the debounced writer from flushing the old snapshot back to disk.
        Interlocked.Exchange(ref _cacheDirty, 0);
        try
        {
            if (File.Exists(CacheFilePath)) File.Delete(CacheFilePath);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "Lyrics:CacheClear");
        }

        _cacheFileLoaded = false;
        CrashLog.WriteNote("LyricsCache", "cleared");
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

    private sealed class QqSearchResponse { [JsonPropertyName("data")] public QqSearchData? Data { get; set; } }
    private sealed class QqSearchData { [JsonPropertyName("song")] public QqSongList? Song { get; set; } }
    private sealed class QqSongList { [JsonPropertyName("list")] public List<QqSong>? List { get; set; } }
    private sealed class QqSong
    {
        [JsonPropertyName("songmid")] public string? SongMid { get; set; }
        [JsonPropertyName("songname")] public string? SongName { get; set; }
        [JsonPropertyName("singer")] public List<QqSinger>? Singers { get; set; }
        [JsonPropertyName("interval")] public double Interval { get; set; }
    }
    private sealed class QqSinger { [JsonPropertyName("name")] public string? Name { get; set; } }
    private sealed class QqLyricResponse
    {
        [JsonPropertyName("lyric")] public string? Lyric { get; set; }
        [JsonPropertyName("trans")] public string? Trans { get; set; }
    }

    private sealed class AggregatorSong
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("artist")] public List<string?>? Artist { get; set; }
        [JsonPropertyName("album")] public string? Album { get; set; }
        [JsonPropertyName("lyric_id")] public string? LyricId { get; set; }
    }
    private sealed class AggregatorLyric
    {
        [JsonPropertyName("lyric")] public string? Lyric { get; set; }
        [JsonPropertyName("tlyric")] public string? TLyric { get; set; }
    }

    #endregion
}
