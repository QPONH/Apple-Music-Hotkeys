using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AppleMusicOverlay.Services;

/// <summary>One track of the playlist whose lyrics are cached offline.</summary>
public sealed record PlaylistTrack(string Title, string Artist, string Album, double Seconds = 0);

/// <summary>
/// Downloads and maintains the offline lyric cache for a playlist ("收藏夹").
/// The playlist can be pasted as text (one "Title - Artist" line each) or as an Apple Music
/// playlist/album link, whose page embeds the track list as JSON.
/// </summary>
public sealed class PlaylistCacheService
{
    private static readonly string StorePath = Path.Combine(AppPaths.SettingsDirectory, "playlist-cache.json");

    private readonly LyricsService _lyrics;

    public PlaylistCacheService(LyricsService lyrics) => _lyrics = lyrics;

    public IReadOnlyList<PlaylistTrack> LastScanned { get; private set; } = Array.Empty<PlaylistTrack>();

    /// <summary>Parses pasted text or Apple Music links into a de-duplicated track list.</summary>
    public async Task<IReadOnlyList<PlaylistTrack>> ScanAsync(string input, CancellationToken token = default)
    {
        var tracks = new List<PlaylistTrack>();
        string trimmed = (input ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            LastScanned = Array.Empty<PlaylistTrack>();
            return LastScanned;
        }

        // The box can mix several links with plain lines, which is how a long playlist gets covered:
        // an Apple Music page only exposes the tracks it renders, so big libraries are pasted in
        // batches.
        var textLines = new List<string>();
        foreach (string raw in trimmed.Replace("\r", string.Empty).Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                if (textLines.Count > 0)
                {
                    tracks.AddRange(ParseLines(string.Join('\n', textLines)));
                    textLines.Clear();
                }

                try
                {
                    tracks.AddRange(await ScanAppleMusicLinkAsync(line, token));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    CrashLog.Write(ex, "Playlist:Link");
                }
            }
            else
            {
                textLines.Add(line);
            }
        }

        if (textLines.Count > 0) tracks.AddRange(ParseLines(string.Join('\n', textLines)));

        LastScanned = tracks
            .GroupBy(track => LyricsService.CacheKeyFor(track.Title, track.Artist), StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        SaveLastScanned();
        return LastScanned;
    }

    /// <summary>Replaces the scanned list (used when the list came from the Music window instead of text).</summary>
    public void SetScanned(IReadOnlyList<PlaylistTrack> tracks)
    {
        LastScanned = tracks
            .GroupBy(track => LyricsService.CacheKeyFor(track.Title, track.Artist), StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        SaveLastScanned();
    }

    /// <summary>Loads the previous scan so a restart can still prune the cache.</summary>
    public void LoadLastScanned()
    {
        try
        {
            if (!File.Exists(StorePath)) return;
            List<PlaylistTrack>? stored = JsonSerializer.Deserialize<List<PlaylistTrack>>(File.ReadAllText(StorePath));
            LastScanned = stored ?? new List<PlaylistTrack>();
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "Playlist:Load");
        }
    }

    private void SaveLastScanned()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.SettingsDirectory);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(LastScanned));
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "Playlist:Save");
        }
    }

    /// <summary>"Title - Artist" / "Title | Artist" / "Title\tArtist", one per line.</summary>
    private static IEnumerable<PlaylistTrack> ParseLines(string text)
    {
        foreach (string raw in text.Replace("\r", string.Empty).Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            string title = line;
            string artist = string.Empty;
            foreach (string separator in new[] { " | ", "\t", " - ", " – ", " — " })
            {
                int index = line.LastIndexOf(separator, StringComparison.Ordinal);
                if (index <= 0) continue;
                title = line[..index].Trim();
                artist = line[(index + separator.Length)..].Trim();
                break;
            }

            if (title.Length == 0) continue;
            yield return new PlaylistTrack(title, artist, string.Empty);
        }
    }

    /// <summary>
    /// Reads the playlist/album page and extracts its tracks. Apple embeds the catalogue data in the
    /// HTML as JSON objects carrying "artistName" next to the track's "name".
    /// </summary>
    private static async Task<List<PlaylistTrack>> ScanAppleMusicLinkAsync(string url, CancellationToken token)
    {
        var tracks = new List<PlaylistTrack>();
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
        string html = await client.GetStringAsync(url, token);

        // Apple embeds each track as an object where "artistName" is followed (within a few hundred
        // bytes, inside impressionMetrics) by "name": the song title. The name is looked up *after*
        // the artist so a playlist-owner name earlier in the page is never mistaken for a track.
        foreach (Match match in Regex.Matches(html, "\"artistName\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\""))
        {
            string artist = JsonUnescape(match.Groups[1].Value);
            if (artist.Length == 0) continue;

            int from = match.Index + match.Length;
            int to = Math.Min(html.Length, from + 3000);
            Match title = Regex.Match(html[from..to], "\"name\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (!title.Success) continue;

            string name = JsonUnescape(title.Groups[1].Value);
            if (name.Length == 0 || name.StartsWith("impression", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.All(char.IsDigit)) continue;
            tracks.Add(new PlaylistTrack(name, artist, string.Empty));
        }

        return tracks;
    }

    private static string JsonUnescape(string value) =>
        WebUtility.HtmlDecode(Regex.Unescape(value)).Trim();

    /// <summary>Downloads lyrics for every track that is not cached yet. Returns (cached, failed).</summary>
    public async Task<(int Cached, int Failed)> DownloadAsync(
        IReadOnlyList<PlaylistTrack> tracks, IProgress<string>? progress, CancellationToken token)
    {
        int cached = 0, failed = 0;
        var existing = new HashSet<string>(_lyrics.GetCachedKeys(), StringComparer.Ordinal);

        foreach (PlaylistTrack track in tracks)
        {
            if (token.IsCancellationRequested) break;
            string key = LyricsService.CacheKeyFor(track.Title, track.Artist);
            if (existing.Contains(key))
            {
                cached++;
                continue;
            }

            progress?.Report($"{cached + failed + 1}/{tracks.Count}：{track.Title}");
            try
            {
                LyricsLookupResult result = await _lyrics.GetLyricsAsync(
                    track.Title, track.Artist, string.IsNullOrWhiteSpace(track.Album) ? null : track.Album,
                    TimeSpan.Zero, forceRefresh: false, wantTranslation: true, token: token);
                if (result.Lyrics is not null && !result.Lyrics.Placeholder)
                {
                    cached++;
                    existing.Add(key);
                }
                else
                {
                    failed++;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "Playlist:Download");
                failed++;
            }
        }

        return (cached, failed);
    }

    /// <summary>Removes every cached lyric that does not belong to the given playlist.</summary>
    public int PruneAsync(IReadOnlyList<PlaylistTrack> tracks)
    {
        var keep = new HashSet<string>(
            tracks.Select(track => LyricsService.CacheKeyFor(track.Title, track.Artist)),
            StringComparer.Ordinal);
        return _lyrics.RemoveCachedKeysExcept(keep);
    }
}
