using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AppleMusicOverlay.Services;

public sealed record LyricLine(TimeSpan Start, string Text);
public sealed record LyricsResult(string Title, string Artist, IReadOnlyList<LyricLine> Lines);

public sealed class LyricsService
{
    private static readonly HttpClient Client = CreateClient();
    private readonly SmtcMediaSessionService _mediaService;

    public LyricsService(SmtcMediaSessionService mediaService) => _mediaService = mediaService;
    public Task<PlaybackSnapshot?> GetCurrentPlaybackAsync(CancellationToken token = default) => _mediaService.GetCurrentPlaybackAsync(token);

    public async Task<LyricsResult?> GetLyricsAsync(string title, string artist, TimeSpan duration, CancellationToken token = default)
    {
        foreach (string candidate in GetTitleVariants(title))
        {
            var data = await TryLrcLibAsync(candidate, artist, token)
                ?? await TryNeteaseAsync(candidate, artist, token)
                ?? await TryLyricsOvhAsync(candidate, artist, token);
            if (data is null) continue;
            var lines = !string.IsNullOrWhiteSpace(data.SyncedLyrics)
                ? ParseLrc(data.SyncedLyrics)
                : SpreadPlainLyrics(data.PlainLyrics, duration);
            if (lines.Count > 0) return new LyricsResult(title, artist, lines);
        }
        return null;
    }

    private static IEnumerable<string> GetTitleVariants(string title)
    {
        yield return title.Trim();
        string withoutBrackets = Regex.Replace(title, @"\s*[\(\[（【].*?[\)\]）】]\s*", " ").Trim();
        if (!string.Equals(withoutBrackets, title, StringComparison.Ordinal)) yield return withoutBrackets;
        int separator = withoutBrackets.IndexOf(" - ", StringComparison.Ordinal);
        if (separator > 0) yield return withoutBrackets[..separator].Trim();
    }

    private static async Task<LrcLibResponse?> TryLrcLibAsync(string title, string artist, CancellationToken token)
    {
        try
        {
            string query = $"track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist)}";
            using var exact = await Client.GetAsync($"https://lrclib.net/api/get?{query}", token);
            if (exact.IsSuccessStatusCode)
            {
                var result = await exact.Content.ReadFromJsonAsync<LrcLibResponse>(token);
                if (!string.IsNullOrWhiteSpace(result?.SyncedLyrics) || !string.IsNullOrWhiteSpace(result?.PlainLyrics)) return result;
            }
            using var search = await Client.GetAsync($"https://lrclib.net/api/search?{query}", token);
            if (!search.IsSuccessStatusCode) return null;
            return (await search.Content.ReadFromJsonAsync<List<LrcLibResponse>>(token))?.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.SyncedLyrics) || !string.IsNullOrWhiteSpace(x.PlainLyrics));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            CrashLog.Write(ex, "Lyrics:LRCLIB");
            return null;
        }
    }

    private static async Task<LrcLibResponse?> TryLyricsOvhAsync(string title, string artist, CancellationToken token)
    {
        try
        {
            using var response = await Client.GetAsync($"https://api.lyrics.ovh/v1/{Uri.EscapeDataString(artist)}/{Uri.EscapeDataString(title)}", token);
            if (!response.IsSuccessStatusCode) return null;
            var result = await response.Content.ReadFromJsonAsync<LyricsOvhResponse>(token);
            return string.IsNullOrWhiteSpace(result?.Lyrics) ? null : new LrcLibResponse { PlainLyrics = result.Lyrics };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            CrashLog.Write(ex, "Lyrics:LyricsOvh");
            return null;
        }
    }

    private static async Task<LrcLibResponse?> TryNeteaseAsync(string title, string artist, CancellationToken token)
    {
        try
        {
            string searchUrl = $"https://music.163.com/api/search/get/web?type=1&limit=10&s={Uri.EscapeDataString(title)}";
            using var search = await Client.GetAsync(searchUrl, token);
            if (!search.IsSuccessStatusCode) return null;
            var results = await search.Content.ReadFromJsonAsync<NeteaseSearchResponse>(token);
            var song = results?.Result?.Songs?.OrderByDescending(song => song.Artists?.Any(a => artist.Contains(a.Name ?? string.Empty, StringComparison.OrdinalIgnoreCase)) == true).FirstOrDefault();
            if (song is null) return null;
            using var lyrics = await Client.GetAsync($"https://music.163.com/api/song/lyric?id={song.Id}&lv=-1&kv=-1&tv=-1", token);
            if (!lyrics.IsSuccessStatusCode) return null;
            var data = await lyrics.Content.ReadFromJsonAsync<NeteaseLyricsResponse>(token);
            string? synced = data?.Lrc?.Lyric;
            return string.IsNullOrWhiteSpace(synced) ? null : new LrcLibResponse { SyncedLyrics = synced };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            CrashLog.Write(ex, "Lyrics:Netease");
            return null;
        }
    }

    private static List<LyricLine> ParseLrc(string lrc)
    {
        var result = new List<LyricLine>();
        int offsetMilliseconds = 0;
        var offsetMatch = Regex.Match(lrc, @"\[offset:\s*([+-]?\d+)\s*\]", RegexOptions.IgnoreCase);
        if (offsetMatch.Success) int.TryParse(offsetMatch.Groups[1].Value, out offsetMilliseconds);
        var timestamp = new Regex(@"\[(\d{1,3}):(\d{1,2}(?:[.:]\d{1,3})?)\]");
        foreach (string raw in lrc.Replace("\r", string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var matches = timestamp.Matches(raw);
            if (matches.Count == 0) continue;
            string text = timestamp.Replace(raw, string.Empty).Trim();
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
        if (string.IsNullOrWhiteSpace(plainLyrics)) return new();
        var lines = plainLyrics.Replace("\r", string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0) return new();
        double seconds = duration.TotalSeconds > 0 ? duration.TotalSeconds / lines.Length : 4;
        return lines.Select((text, index) => new LyricLine(TimeSpan.FromSeconds(index * seconds), text)).ToList();
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MusicFloat/14.18");
        return client;
    }

    private sealed class LrcLibResponse
    {
        [JsonPropertyName("syncedLyrics")] public string? SyncedLyrics { get; set; }
        [JsonPropertyName("plainLyrics")] public string? PlainLyrics { get; set; }
    }

    private sealed class LyricsOvhResponse
    {
        [JsonPropertyName("lyrics")] public string? Lyrics { get; set; }
    }

    private sealed class NeteaseSearchResponse { [JsonPropertyName("result")] public NeteaseSearchResult? Result { get; set; } }
    private sealed class NeteaseSearchResult { [JsonPropertyName("songs")] public List<NeteaseSong>? Songs { get; set; } }
    private sealed class NeteaseSong { [JsonPropertyName("id")] public long Id { get; set; } [JsonPropertyName("artists")] public List<NeteaseArtist>? Artists { get; set; } }
    private sealed class NeteaseArtist { [JsonPropertyName("name")] public string? Name { get; set; } }
    private sealed class NeteaseLyricsResponse { [JsonPropertyName("lrc")] public NeteaseLrc? Lrc { get; set; } }
    private sealed class NeteaseLrc { [JsonPropertyName("lyric")] public string? Lyric { get; set; } }
}
